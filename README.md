# Install Other OS

Run a full Linux (Debian) inside an app on your **Xbox Series X|S in Developer Mode**.

The app uses [QEMU](https://www.qemu.org/) 10.0.0 to emulate a PC in software. No hypervisor is available to Xbox apps, so it is much slower than a real PC, but it is good enough for a light desktop (XFCE) and for small servers.

![platform](https://img.shields.io/badge/platform-Xbox%20Dev%20Mode%20(UWP)-107C10)

## What you need
- An Xbox Series X|S with **Developer Mode** turned on and the **Device Portal** enabled
- A PC or phone on the same network (to use the Device Portal in a web browser)
- A Debian ISO (for example the *netinst* image) to install from
- Optional: a USB keyboard. The Xbox pointer works as a mouse inside Linux.

## Download
No GitHub account needed: open the **[Releases page](../../releases/latest)** and download
- the `.msix` file, and
- the three x64 `.appx` dependency files.

(Files shown on a run's *Actions* page need a GitHub login. The Releases page does not.)

## Install on the Xbox
1. On your PC open the Device Portal: `https://<your-xbox-ip>:11443`.
2. Open **My games & apps** (Apps manager) and use the **Add / Deploy** option.
3. Choose the `.msix` file, add the three x64 `.appx` files as dependencies, then start the install.
4. Installing a newer version over an older one keeps your virtual disk. Installing an older version over a newer one is not possible without uninstalling first, and uninstalling **deletes the virtual disk**.

## First start
1. Open **Install Other OS** on the Xbox, then press **Settings**.
2. Pick a disk size and press **Create virtual disk**.
3. Put a Debian ISO into the app's folder: in the Device Portal go to *File explorer > LocalAppData > Microsoft.MicrosoftEdge.XboxVmHost_xkj04xcm6e7n4 > LocalState* and upload it. (Or use **Pick ISO file** in Settings to copy one from another folder or drive.)
4. Turn **Network** on in Settings if you want internet inside Linux.
5. Back on the main menu press **Boot from ISO** and install Debian. Use the text installer and choose a minimal software selection.
6. After the install, use **Start Linux from disk**.

## Menu
| Button | What it does |
| --- | --- |
| Resume saved state | Continue exactly where you pressed the save hotkey |
| Start Linux from disk | Boot the installed system (asks before throwing away a saved state) |
| Boot from ISO | Boot the newest ISO in the app folder, to install or try Linux |
| Show storage folder | Lists the files in the app's storage |
| Settings | CPU type, code cache, CPUs, virtual RAM, network, disk and screen size |

The defaults are the recommended settings. Change one setting at a time.

## Keys while Linux is running
Hold **Ctrl + Alt + Shift**, then press:
- **V** save the state of the virtual PC and return to the menu
- **S** take a screenshot (saved in LocalState\screenshots)
- **Q** stop the virtual PC
- **D** show or hide the input counters

## Save state
Ctrl + Alt + Shift + V stops the virtual PC, saves its memory and devices to a file, and returns to the menu. **Resume saved state** loads it again, even after the app was closed. There is one saved state at a time. It is used up when you resume it, because Linux changes the disk afterwards. It is only valid for the same app version and an unchanged disk file.

## Good to know
- The virtual PC stops when the app leaves the foreground (Guide button, Home). Use save state first.
- QEMU can run once per app start; the app restarts itself when the virtual PC ends.
- There is no GPU acceleration. A light desktop such as XFCE works; heavy 3D does not.
- The Linux network card can change name when virtual devices change (for example `ens3` becomes `ens4`). If the network stops working after changing settings, look at `ip a` and fix the name in `/etc/network/interfaces`.
- Memory limit is set by the Xbox. Four CPUs or very large RAM settings can crash the app.

## Build it yourself
Fork the repository and run **Actions > Build Xbox UWP package > Run workflow**.
- *Include the QEMU DLL*: leave ticked for the first build (about 25 minutes).
- Untick it and give a `reuse_run` number from one of your own earlier runs for a quick app-only build.
- *Publish a Release*: tick it to publish the finished files on your Releases page.

The build downloads the unmodified QEMU v10.0.0 source from upstream, builds it with MSYS2, and turns it into a DLL using the files in `native/`. The workflow signs the package with a throw-away test certificate.

## Contact
Steam: [YoCanIGetUhhhh](https://steamcommunity.com/profiles/76561198072420605)

Progress posts: [Xbox VM running Linux](https://www.reddit.com/r/XboxRetailHomebrew/comments/1x1xrli/xbox_vm_running_linux/) and [Xbox VM update](https://www.reddit.com/r/XboxRetailHomebrew/comments/1x2pidd/xboxvmupdate/) on r/XboxRetailHomebrew.

## License and credits
This project is released under the **GNU General Public License v2** (see `LICENSE`), the same license as QEMU.
- [QEMU](https://www.qemu.org/) (GPL-2.0) and [libslirp](https://gitlab.freedesktop.org/slirp/libslirp) (BSD) provide the emulation and the user-mode network.
- Tux the penguin is the Linux mascot.
