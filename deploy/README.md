# Production deployment

`Publish to production` is a manual GitHub Actions workflow on `main`. It restores, builds, tests, publishes, copies a release to one DigitalOcean Droplet, makes an online SQLite backup, restarts the app, and checks the local and public health endpoints. The app runs as a systemd service on Ubuntu 24.04 x64. A named Cloudflare Tunnel serves the public HTTPS hostname and forwards to the app on `127.0.0.1:5000`. This keeps web ports closed on the Droplet and supports Blazor's WebSocket connection.

## One-time setup

1. Create an Ubuntu 24.04 x64 Droplet with SSH key authentication. Enable DigitalOcean backups and monitoring. Start with at least 2 GB RAM and watch memory and disk use during the initial Scryfall catalog import. Configure a DigitalOcean cloud firewall with inbound TCP 22 from the Internet for the GitHub-hosted runner and your administration access; leave inbound 80/443 closed. Standard GitHub-hosted runners do not have a fixed individual IP, so restricting SSH only to your home IP will block the workflow. Disable SSH password authentication and root login after confirming your non-root sudo user works.
2. Generate a **separate** Ed25519 deploy key on your computer. A passphrase is supported when you add it as `PROD_SSH_PASSPHRASE` in GitHub; leave the passphrase empty only if you prefer an unencrypted deploy key. Copy its public key and the `deploy` directory to your existing sudo user on the Droplet. For example, from PowerShell at the repository root (replace the uppercase placeholders):

   ```powershell
   ssh-keygen -t ed25519 -f "$HOME\.ssh\playmagic_prod" -C playmagic-deploy
   scp -r deploy ADMIN_USER@DROPLET_IP:/tmp/playmagic-deploy
   scp "$HOME\.ssh\playmagic_prod.pub" ADMIN_USER@DROPLET_IP:/tmp/playmagic-deploy.pub
   ssh ADMIN_USER@DROPLET_IP "sudo bash /tmp/playmagic-deploy/bootstrap.sh /tmp/playmagic-deploy.pub"
   ```

   Bootstrap installs the ASP.NET Core 10 runtime, `rsync`, and SQLite tools, then creates the app service, data directory, and restricted `playmagic-deploy` SSH account. The app is bound only to loopback. The deploy account can restart only `playmagic.service` through passwordless sudo. Check that `ssh -i "$HOME\.ssh\playmagic_prod" playmagic-deploy@DROPLET_IP` works before configuring GitHub.
3. In Cloudflare, create a **named, remotely managed Tunnel** for a domain on your Cloudflare account. Install `cloudflared` on the Droplet using Cloudflare's Ubuntu instructions, then run the dashboard's `sudo cloudflared service install <TUNNEL_TOKEN>` command on the Droplet. Keep the tunnel token on the Droplet, outside this repository and GitHub Actions. Add a **Published application** route for your public hostname, with service URL `http://127.0.0.1:5000`. Cloudflare creates the proxied DNS route. Enable WebSockets and redirect HTTP visitors to HTTPS in Cloudflare. Allow unauthenticated GET requests to `/healthz` through any Cloudflare Access or challenge rule you add, since the workflow checks that URL after deploying.
4. In GitHub repository **Settings → Environments**, create `production` and restrict deployment branches to `main`. Add these placeholders with your real values:

   | Type | Name | Value |
   | --- | --- | --- |
   | Variable | `PROD_HOST` | Droplet public IPv4 address or SSH hostname (not the proxied app hostname) |
   | Variable | `PROD_URL` | Public HTTPS URL, such as `https://playmagic.example.com` |
   | Secret | `PROD_SSH_KEY` | Entire private key from `playmagic_prod`, including BEGIN/END lines |
   | Secret (optional) | `PROD_SSH_PASSPHRASE` | Passphrase for `PROD_SSH_KEY`, if the private key is encrypted |
   | Secret | `PROD_SSH_KNOWN_HOSTS` | Pinned OpenSSH host-key line for `PROD_HOST`, for example `DROPLET_IP ssh-ed25519 AAAA...` |

   Obtain the host-key line with `ssh-keyscan -t ed25519 DROPLET_IP`, then verify its fingerprint against the DigitalOcean console's `ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub` output before saving it. Do not use an unverified `ssh-keyscan` result as the trust anchor. The SSH private key is configured only after build and tests pass.
5. Run **Actions → Publish to production → Run workflow** from `main`. First deployment creates the database and runs EF migrations. The Scryfall catalog imports in the background, so cards may take a while to populate. Check `https://YOUR_HOST/healthz` and the home page, then verify a multiplayer connection works.

## Operations

- Service logs: `sudo journalctl -u playmagic -f`. Tunnel logs: `sudo journalctl -u cloudflared -f`.
- Release files: `/opt/playmagic/releases`; current release: `/opt/playmagic/current`.
- Durable SQLite data: `/var/lib/playmagic/playmagic.db`. The release directory is disposable. Each deployment after the first makes an online SQLite backup in `/opt/playmagic/backups` before switching the binary. Monitor disk space and remove old releases and backups after confirming newer ones are good. Keep off-Droplet backups enabled as well.
- If the new process fails its local health check, the script switches back to the previous binary and reports failure. EF migrations run at startup and may change the database; a binary rollback cannot undo a migration. Restore the pre-deploy SQLite backup if the previous binary cannot run against the new schema.
- The app expects one instance. Its in-process notifications and rate limits are not designed for multiple app servers. Cloudflare's `CF-Connecting-IP` and `X-Forwarded-Proto` are trusted only from the loopback tunnel process, so per-visitor rate limits and HTTPS behavior work through the tunnel.
- If deployment says `Permission denied (publickey)`, confirm `PROD_SSH_KEY` contains the **private** key, set `PROD_SSH_PASSPHRASE` if it is encrypted, and test `ssh -i "$HOME\.ssh\playmagic_prod" playmagic-deploy@DROPLET_IP` from your computer. If that login fails too, compare the matching public key with `/home/playmagic-deploy/.ssh/authorized_keys` on the Droplet.

## References

- [DigitalOcean production Droplet setup](https://docs.digitalocean.com/products/droplets/getting-started/recommended-droplet-setup/)
- [Cloudflare Tunnel setup](https://developers.cloudflare.com/tunnel/get-started/) and [Ubuntu package installation](https://developers.cloudflare.com/tunnel/features/locally-managed-tunnels/create-local-tunnel/)
- [Cloudflare Tunnel WebSocket support](https://developers.cloudflare.com/cloudflare-one/faq/cloudflare-tunnels-faq/) and [origin request headers](https://developers.cloudflare.com/fundamentals/reference/http-headers/)
- [ASP.NET Core forwarded headers](https://learn.microsoft.com/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0)
- [GitHub environment secrets and deployment branches](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments)
- [GitHub-hosted runner IP ranges](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)
