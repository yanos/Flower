# Listening away from home

Set up your server on your local network first. Remote access lets paired devices stream from it when they're elsewhere. Flower remembers server addresses and reconnects when one is reachable.

Direct port forwarding works with the installed apps and doesn't require a relay service. Tailscale and Cloudflare are separate services; the table below explains when each method is useful.

| Method | Use | Requirements |
|---|---|---|
| [Direct port forwarding](#port-forwarding-for-flower-apps) | No relay service; Flower apps | Public IP and forwarded HTTPS port |
| [Tailscale](#remote-access-with-tailscale) | Private access | Tailscale on the server and listening devices |
| [Caddy](#port-forwarding-with-caddy) | Browser access with trusted HTTPS | Domain, public IP, forwarded ports |
| [Cloudflare Tunnel](#cloudflare-tunnel) | No router port forwarding | Cloudflare domain and tunnel |

Tailscale keeps your server on a private network. The other methods make it publicly reachable, but only paired devices can access the music library. Keep your server updated and use HTTPS for public connections.

Docker examples below use the repository's `docker/` directory. The Caddy and
Cloudflare Compose files use Linux host networking. For macOS or Windows,
run the proxy and server directly, or use a Linux host; don't expose the
Docker VM's bridged ports directly to the internet.

## Remote access with Tailscale

### Flower apps

1. [Install Tailscale](https://tailscale.com/download) on the server and every
   listening device. Connect them to the same Tailscale network.
2. Leave **Allow connections from the internet** off in Flower's server settings.
   Tailscale's address range is allowed by default.
3. Pair using a Flower invite link. You can pair at home or remotely, provided
   the invite contains a reachable address.
4. Turn off Wi-Fi on your phone, keep Tailscale connected and try playing a song.

On a Linux Docker host, install Tailscale on the host; no additional Compose
file is needed. If the app was paired before Tailscale was installed, let it
connect at home once to learn the new address, or add the Tailscale address
manually in **Settings → Servers** (phone: **Add Server**).

To invite a friend, use [Tailscale machine sharing](https://tailscale.com/kb/1084/sharing)
as well as a Flower pairing invite. They still need Tailscale installed.

### Browser player with Tailscale Serve

For a browser address with a trusted HTTPS certificate, enable MagicDNS and
HTTPS certificates in Tailscale's admin console, then run on the server:

```bash
sudo tailscale serve --bg 4533
```

Use the `https://…ts.net` address it prints. Set the following in
`flower-server.json`, replacing the example hostname:

```json
{
  "Flower": {
    "TrustedProxies": ["127.0.0.1/32"],
    "AdvertisedHost": "https://basement.tail1234.ts.net"
  }
}
```

Restart Flower. `TrustedProxies` identifies the local proxy so Flower can check
the real caller's address. Include `https://` in `AdvertisedHost`; a bare
hostname would use Flower's own HTTP port instead of Tailscale Serve.

Use `tailscale serve status` to inspect the setup. Use **Serve** for private
access; **Funnel** publishes to the internet. See
[Tailscale Serve documentation](https://tailscale.com/docs/features/tailscale-serve)
for prerequisites and CLI options.

## Port forwarding for Flower apps

Use Flower's built-in HTTPS port to connect directly from the installed apps. No domain, VPN, or third-party relay is required. You do need a public IP address from your internet provider.

1. Reserve the server's LAN address in your router's DHCP settings.
2. Forward **TCP 4534** on the router to **4534** at that address.
3. In the server's Network settings, enable **Allow connections from the internet**.
4. Leave `AdvertisedHost` empty. Flower looks up its public IP and reports it
   to paired devices, checking for changes every fifteen minutes.
5. Test from cellular with Wi-Fi off, then send new listeners the app invite link.

**Keep port 4533 private.** It serves unencrypted HTTP.

A paired app checks the built-in certificate against the server identity in
its invite. A browser shows a certificate warning; use Caddy or another
trusted certificate for a browser address you share with listeners.

If you use dynamic DNS or a different external port, set the full public
address yourself:

```json
{
  "Flower": {
    "AdvertisedHost": "https://music.example.com:4534"
  }
}
```

A device learns a changed public IP only while it can still reach the server.
If it loses all known addresses, connect it at home or add a working address
manually. Dynamic DNS avoids depending on the old public IP.

Port forwarding requires a public address from your ISP. Compare the router's
WAN address with the public IP shown in Flower's Network page. A private WAN
address, or one in `100.64.0.0/10`, usually means another NAT is upstream. Ask
your ISP about a public IP, or use Tailscale or a tunnel.

Some work or hotel networks block port 4534. See
[Using HTTPS on port 443](SERVER-REFERENCE.md#using-https-on-port-443).

## Port forwarding with Caddy

Use Caddy when listeners need a browser address with a trusted HTTPS certificate. It handles certificate setup and renewal for your domain.

1. Point a DNS name, such as `music.example.com`, at your router's public IP.
   Use dynamic DNS if that IP changes.
2. Reserve the server's LAN address. Forward **TCP 80 and 443** to it.
   This supports certificate validation and HTTP-to-HTTPS redirects.
3. In `docker/.env`, add:

   ```dotenv
   FLOWER_HOSTNAME=music.example.com
   ```

4. Start the Linux Docker deployment:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.caddy.yml up -d
   ```

5. In Flower's server Network settings, enable **Allow connections from the internet**.
6. Open `https://music.example.com` from outside your home network and pair
   the app or browser using its invite.

The Compose file sets `TrustedProxies` and `AdvertisedHost`. Keep
`docker/caddy-data` and `docker/caddy-config`: they hold Caddy's certificates
and account state. Back them up along with Flower's data.

Without Docker, [install Caddy](https://caddyserver.com/docs/install) on the
server and use this Caddyfile:

```caddyfile
music.example.com {
    reverse_proxy localhost:4533
}
```

Configure Flower, then restart it:

```json
{
  "Flower": {
    "AllowPublicAccess": true,
    "TrustedProxies": ["127.0.0.1/32"],
    "AdvertisedHost": "https://music.example.com"
  }
}
```

Don't forward 4533. The proxy should reach it locally. If the proxy runs on
another machine, use its address in `TrustedProxies` and its reachable Flower
address in the proxy configuration. See
[Caddy's HTTPS requirements](https://caddyserver.com/docs/automatic-https).

## Cloudflare Tunnel

Cloudflare Tunnel works without router port forwarding, including behind carrier-grade NAT. Listeners open your hostname without installing a VPN. **Traffic passes through Cloudflare**, so this option is not fully independent of third parties.

Cloudflare decrypts traffic at its edge. Its CDN terms also allow restrictions
on disproportionate audio and large-file delivery; check that your intended
use is covered before relying on it for music. See
[Cloudflare's service terms](https://www.cloudflare.com/service-specific-terms-application-services/).

### Linux Docker

1. Add your domain to Cloudflare, then
   [create a remotely managed tunnel](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/get-started/create-remote-tunnel/).
2. In the tunnel's published application route, map your hostname, such as
   `music.example.com`, to **HTTP → localhost:4533**.
3. Put the hostname and tunnel token in `docker/.env`:

   ```dotenv
   FLOWER_HOSTNAME=music.example.com
   TUNNEL_TOKEN=your-tunnel-token
   ```

4. Start the deployment:

   ```bash
   docker compose -f docker-compose.yml -f docker-compose.cloudflared.yml up -d
   ```

5. In Flower's server Network settings, enable **Allow connections from the internet**.
6. Open `https://music.example.com` and send listeners their app or browser invites.

The override sets `TrustedProxies` and `AdvertisedHost`. Keep `.env` private;
the tunnel token is a credential.

### Without Docker

Install `cloudflared` using Cloudflare's tunnel setup instructions and route
the public hostname to `http://localhost:4533`. Use the same Flower JSON
settings shown in the Caddy section, with your tunnel hostname, and restart
Flower. Run `cloudflared` as a service so the tunnel survives a reboot.

## Checking the connection

To test remote access, turn off Wi-Fi on your phone and use cellular data. A test from inside your home can fail if your router doesn't support connections back through its public IP.

| Check | Expected result |
|---|---|
| Play a song, seek and let the next song start | Playback works from outside |
| Browser access, if configured | HTTPS opens without a certificate warning |
| Flower's Network page | Shows the intended public or Tailscale address |
| Logs behind a proxy | No warning that `X-Forwarded-For` is being ignored |
| Public deployment | **Allow connections from the internet** is on |
| Tailscale-only deployment | That switch stays off; both devices are connected to Tailscale |

A public reachability warning can be a false alarm if your router can't loop
traffic back. The cellular test is the deciding check.

For an ignored forwarded-address warning, set `TrustedProxies` to the actual
proxy address and restart. Trust only your proxy, never every network.
Verified devices have separate request limits; anonymous requests and failed
signatures still depend on the correct source address.
