using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Flower.Tests.TestSupport
{
    // Serves one file over loopback HTTP, so a test can hand LibVLC a real
    // URL rather than a path. Exists because "is this track local or
    // streamed" turned out to be a distinction TrackDecoder got wrong for
    // its whole life and no test could see - every fixture was a file.
    //
    // Deliberately the smallest thing that answers a GET: LibVLC only needs
    // the bytes and a length, and a test server that needs configuring is a
    // test server nobody adds a case to.
    internal sealed class LocalFileServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly byte[] _content;

        public LocalFileServer(string filePath)
        {
            _content = File.ReadAllBytes(filePath);

            // Port 0 is not available to HttpListener, so take a free one
            // from the OS by binding a socket and letting it go.
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/{Path.GetFileName(filePath)}";

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    // Disposed mid-wait, which is how this loop ends.
                    return;
                }

                try
                {
                    context.Response.ContentType = "audio/wav";
                    context.Response.ContentLength64 = _content.Length;

                    // HEAD and byte-range requests both matter to LibVLC: it
                    // probes before it reads. Answering the whole file to a
                    // range request is legal enough for a demuxer that is
                    // only trying to establish seekability.
                    if (context.Request.HttpMethod != "HEAD")
                        await context.Response.OutputStream.WriteAsync(_content);
                }
                catch (Exception)
                {
                    // A client that hangs up mid-body is normal here.
                }
                finally
                {
                    context.Response.Close();
                }
            }
        }

        private static int FreePort()
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        public void Dispose()
        {
            if (_listener.IsListening)
                _listener.Stop();

            _listener.Close();
        }
    }
}
