# Finding the host on the local network

A till no longer needs to be told the host's address. The sign-in window settles it on its own:

1. the address used last time, if it still answers (1.5 s);
2. otherwise whatever answers on the network (2 s);
3. only if neither works does the address field appear. "Modifier" brings it back at any time, and
   if several hosts answer, they are listed so the right one can be typed.

## How

A till broadcasts `LONNII-DISCOVER/1` over **UDP port 5281** (to the general broadcast address, to each
network card's own broadcast address, and to itself). The host (`LanDiscoveryService`) replies, to the
asker only, `LONNII-HOST/1|<api port>|<computer name>`. Nothing else is revealed - no shop name, no
data - and signing in still needs an account.

## Settings (host)

`Lonnii:Discovery:Enabled` (default true), `Lonnii:Discovery:Port` (default 5281).

## When it does not find the host

- **Windows Firewall on the host** must allow inbound **UDP 5281** as well as TCP 5280. This is the
  usual cause. The symptom is the old one: the address field appears.
- Guest Wi-Fi / "client isolation" and some routers drop broadcasts. Type the host's address once; it is
  remembered.
- The port may be held by another program: the host logs "Découverte réseau désactivée" and carries on.
