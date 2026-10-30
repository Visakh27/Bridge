# Remote access: running your own relay

Bridge normally connects your phone and PC directly over your own Wi-Fi.
Remote access lets them reach each other from anywhere, for example when your
phone is on mobile data, through a small **relay** that you run yourself on any
server. Remote access is off until you turn it on.

## What the relay can and can't see

Everything between phone and PC stays **end-to-end encrypted** with the same
handshake and keys as on your Wi-Fi. The relay only passes bytes along, and it
can't decrypt them:

- **It can't see** file contents or names, clipboard text, notifications,
  links, photos, device names, or Bridge's keys.
- **It can see** the IP addresses that connect to it, when they connect, how
  much data flows, and a random-looking *room* id for each phone–PC pair.

The relay has its own **relay key**. Only devices that prove they hold it can
use the relay. The key itself is never sent: each join carries a one-time
proof made from it. The key decides who can use the relay. It isn't the key
that protects your data, so someone who learns it can use your relay but still
can't read anything passing through.

## Run the relay

The relay is in `windows/Bridge.Relay`. It's a .NET 8 console app with no
dependencies beyond the runtime, and it runs on Linux, Windows or macOS.

### With Docker

Run these from the `windows/` folder:

```sh
docker build -f Bridge.Relay/Dockerfile -t bridge-relay .
docker run -d --name bridge-relay --restart unless-stopped \
  -p 47830:47830 -v bridge-relay:/data bridge-relay relay.example.com
docker logs bridge-relay        # shows the relay link to paste into Bridge
```

### Without Docker

```sh
dotnet run --project Bridge.Relay -c Release -- relay.example.com
```

### Settings

Environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `BRIDGE_RELAY_PORT` | `47830` | TCP port to listen on |
| `BRIDGE_RELAY_KEY` | *(created once)* | The relay key, 32 bytes in base64url (43 characters) |
| `BRIDGE_RELAY_DATA` | `./data`, or `/data` in Docker | Where a created key is kept (`relay.key`) |

The optional argument (`relay.example.com` above) is the address your devices
will use to reach the server. It only fills in the printed link.

Open the port (TCP 47830 by default) in the server's firewall. The relay speaks
its own small protocol over TCP. Put it straight on the internet; it doesn't go
behind a web server or an HTTP proxy.

## Turn it on in Bridge

1. On the PC, open **Settings › Remote access**, paste the link the relay
   printed (`bridge-relay://relay.example.com:47830#…`), and select
   **Use this relay**. The status line should say **On** within a few seconds.
2. Connect each phone to the PC once on the same Wi-Fi. That's when the phone
   learns about the relay; it's sent over their encrypted connection, never in
   a QR code.
3. Away from home, the phone first tries the PC directly and then local
   discovery. If neither works, it goes through the relay. When it's back on
   Wi-Fi, it switches back to a direct connection.

To stop using the relay, turn **Remote access** off or select **Remove** on
the PC. Phones are told the next time they connect.

New phones are paired only on the same network: the PC refuses pairing that
comes through the relay.

## Limits

The relay closes or refuses connections in these cases:

- It serves at most 512 connections at once, and at most 32 from any one
  address.
- A client must send its join line within 10 seconds.
- A connection with no traffic for 2 minutes is closed. Bridge's own
  keep-alives arrive every 15 seconds, so a live connection never hits this.
- Each PC keeps one waiting connection per paired phone. The relay pings these
  every 25 seconds.
- A join proof is accepted once, and only within 2 minutes of the time it
  carries, so the server's clock should be roughly right (NTP).

## Logs

The relay logs counts and reasons only (`relay.paired pipes=1 waiting=2`,
`relay.refused unauthorized`). It never logs addresses, rooms or traffic.
