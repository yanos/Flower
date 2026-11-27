using System;
using System.Linq;

using Flower.Services;

using Makaretu.Dns;

using Xunit;

namespace Flower.Tests.Network;

// The one place the real multicast backend is exercised. Every other discovery
// test runs against FakeMdnsBackend, which is why nothing caught the regression
// this file exists for: MulticastService.Start() used to be reached only through
// Advertise(), so once clients stopped advertising themselves - Flower.Server is
// the only server - the very first Browse() threw "Exceeds max packet size of 0"
// out of App.Bootstrap and took startup down with it.
//
// These open a real multicast socket, deliberately. A mock cannot fail the way
// the library did.
public class MakaretuMdnsBackendTests
{
    [Fact]
    public void Browsing_without_advertising_first_does_not_throw()
    {
        using var backend = new MakaretuMdnsBackend();

        backend.Browse(SyncProtocol.ServiceType);
    }

    [Fact]
    public void Browsing_repeatedly_does_not_throw()
    {
        // NetworkDiscoveryService re-browses on a timer and again on iOS
        // foreground, so every call after the first lands on an already-started
        // service.
        using var backend = new MakaretuMdnsBackend();

        backend.Browse(SyncProtocol.ServiceType);
        backend.Browse(SyncProtocol.ServiceType);
        backend.Browse(SyncProtocol.ServiceType);
    }

    [Fact]
    public void Advertising_after_browsing_does_not_throw()
    {
        // Not a combination Flower itself produces - a client only browses, the
        // server only advertises - but it is the branch where Advertise() has to
        // announce by hand rather than let Start() do it, and a silent break
        // there would only show up as a server nobody can find.
        using var backend = new MakaretuMdnsBackend();

        backend.Browse(SyncProtocol.ServiceType);
        backend.Advertise("flower-test-instance", SyncProtocol.ServiceType, 45999);
        backend.Stop();
    }

    [Fact]
    public void Advertising_does_not_answer_reverse_lookups_for_the_hosts_addresses()
    {
        // The host's mDNS responder (Avahi) owns these PTR records. Answering
        // them too made Avahi see a conflict on every probe and rename the
        // whole machine: lambda.local became lambda-1427.local.
        using var backend = new MakaretuMdnsBackend();
        var address = MulticastService.GetLinkLocalAddresses().First();

        backend.Advertise("flower-test-instance", SyncProtocol.ServiceType, 45999);
        var reverse = backend.Answer(Query(address.GetArpaName(), DnsType.PTR));
        var service = backend.Answer(Query($"flower-test-instance.{SyncProtocol.ServiceType}.local", DnsType.SRV));
        backend.Stop();

        Assert.Empty(reverse.Answers);
        Assert.Single(service.Answers.OfType<SRVRecord>());
    }

    private static Message Query(DomainName name, DnsType type)
    {
        var query = new Message();
        query.Questions.Add(new Question { Name = name, Type = type });
        return query;
    }

    [Fact]
    public void Stopping_twice_does_not_throw()
    {
        // Ordinary shutdown reaches Stop() from the host's stopping phase and
        // again from Dispose; Makaretu's Unadvertise throws the second time.
        var backend = new MakaretuMdnsBackend();

        backend.Advertise("flower-test-instance", SyncProtocol.ServiceType, 45999);
        backend.Stop();
        backend.Dispose();
    }
}
