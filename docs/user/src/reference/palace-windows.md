---
title: Installing Palace on Windows
slug: reference/palace-windows.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Installing Palace on Windows
lede: A step-by-step walk through getting Palace running on Windows, what each step does to your computer, what you will see, and how to undo all of it.
keywords: Palace, Windows, WSL, WSL 2, Windows Subsystem for Linux, Ubuntu, install, apt-get, sudo, virtualization, BIOS, 404 Not Found, uninstall, unregister
---

This page walks you through getting Palace running on Windows, one step at a time. Each step says what
it changes on your computer and what you should see. Read the overview first. It takes two minutes,
and after it none of the steps should surprise you.

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#overview">Before you start: what you are getting into</a></li>
<li><a href="#steps">The steps</a></li>
<li><a href="#normal">Things that look wrong but are normal</a></li>
<li><a href="#problems">If a step fails</a></li>
<li><a href="#undo">Undoing all of it</a></li>
</ol>
</nav>

## Before you start: what you are getting into {#overview}

**Palace is a Linux program.** It does not run natively on Windows. Windows can run Linux programs
through a built-in Microsoft feature called the **Windows Subsystem for Linux (WSL)**. You install
three things, each inside the one before it:

| Layer | What it is | Who makes it | Where it lives |
|---|---|---|---|
| WSL 2 | A Windows feature that runs a small Linux virtual machine | Microsoft, part of Windows | Windows itself |
| Ubuntu | A Linux distribution: the Linux "computer" inside WSL | Canonical, free | A single virtual disk file in your Windows user folder |
| Palace | The 3D solver, built from its source code | Palace's open-source project | A folder in your Linux home, inside Ubuntu |

**What it changes on your computer.**

- **Administrator rights are needed once, to turn WSL on.** After that, nothing needs them. circuitRF
  never asks for administrator rights and never asks for your password.
- **Windows restarts once**, after WSL is turned on.
- **Nothing is installed in Program Files and nothing changes your Windows setup.** Ubuntu and
  everything in it live in one virtual disk file that belongs to your user account. Palace is installed
  only inside Ubuntu.
- **Gmsh and openEMS are not affected.** They run natively on Windows. Only Palace uses the subsystem.

**How long it takes.**

- **Your part is about half an hour**: turning WSL on, restarting, creating a Linux user, and pasting two
  commands.
- **Then the build runs on its own for hours.** Palace is compiled from source, together with about 25
  libraries it depends on. On a fast Mac this took 52 minutes. Inside WSL it has been reported to take
  most of a day. You can keep using circuitRF and your computer while it runs. **Cancel** stops it at any
  point and installs nothing.

**How much disk it uses.** Palace itself took 1.9 GB when measured on a Mac. Ubuntu and the build tools
add a few GB more. Allow about 10 GB free on your Windows drive.

**Two things you will be asked to type.**

- **A Linux user name and password**, the first time Ubuntu starts. They are new. They are not your
  Windows ones, and they are used only inside Ubuntu. Write the password down.
- **That same Linux password**, when a command starts with `sudo`. `sudo` means "run this as the Linux
  administrator". It changes Ubuntu, not Windows.

**Everything can be undone.** [Undoing all of it](#undo) removes Palace, then Ubuntu, then WSL itself.

## The steps {#steps}

### 1. Check that virtualization is on {#step-virtualization}

WSL 2 needs the processor's virtualization feature. Most computers have it turned on already.

Open **Task Manager** (Ctrl+Shift+Esc), choose **Performance**, then **CPU**. Under the graph, look for
**Virtualization: Enabled**.

If it says **Disabled**, it has to be turned on in the computer's firmware settings (BIOS or UEFI), not
in Windows. The setting is usually called Intel VT-x, Intel Virtualization Technology, AMD-V or SVM.
How to reach the firmware settings depends on the computer's maker. Search for your computer model and
"enable virtualization".

### 2. Turn on WSL {#step-wsl}

1. Right-click the **Start** button and choose **Terminal (Admin)**. On older Windows it is
   **Windows PowerShell (Admin)**. Windows asks whether to allow it to make changes: choose **Yes**.
2. Type this and press Enter:

   ```
   wsl --install
   ```

3. When it finishes, restart Windows.

This turns on the WSL feature and, on current Windows, downloads Ubuntu as well. It prints its
progress as it downloads. This is the only step that needs administrator rights.

### 3. Start Ubuntu and create your Linux user {#step-ubuntu}

After the restart, Ubuntu may open by itself. If it does not, open **Ubuntu** from the Start menu. If
there is no Ubuntu in the Start menu, open an ordinary Terminal and run `wsl --install -d Ubuntu`.

The first start takes a minute or two. Then it asks for a **new UNIX username** and a **password**.
Choose any short lower-case name. As you type the password, **nothing appears on the screen, not even
dots**. That is normal: it is being typed. You type it twice.

When you see a prompt ending in `$`, Ubuntu is ready. Leave the window open for the next step.

### 4. Install the build tools inside Ubuntu {#step-tools}

Palace is built from source, so Ubuntu needs a compiler and a few tools. **This is a Linux command, so
it goes in the Ubuntu window**, not in Command Prompt or PowerShell. You are in the right place when the
prompt looks like `yourname@COMPUTER:~$`. If it looks like `C:\Users\…>` or `PS C:\…>`, you are at a
Windows prompt: type `wsl -d Ubuntu` and press Enter first, and the prompt changes.

In the Ubuntu window, paste this line and press Enter (right-click pastes in the terminal):

```
sudo apt-get update && sudo apt-get install build-essential gfortran git python3 patch unzip bzip2 xz-utils file ca-certificates curl
```

If you started the install in circuitRF first, its message shows this same line. Use the line circuitRF
shows if the two ever differ.

- It asks for your **Linux password** from step 3. Again, nothing appears as you type.
- The first half, `apt-get update`, refreshes Ubuntu's list of available packages. The second half
  installs the tools.
- It lists what it will install and asks **Do you want to continue? [Y/n]**. Type `Y` and press Enter.
- It prints many lines as it downloads and unpacks. That takes a few minutes.

It is done when the `$` prompt comes back. You can close the Ubuntu window.

### 5. Install Palace from circuitRF {#step-install}

1. In circuitRF, open **Settings ▸ Solvers**.
2. On the Palace row, choose **Install Palace…**.
3. circuitRF shows what it is about to do: the version, every web address it will download from, the
   folder inside Ubuntu it installs into, and Palace's licence note. **Nothing is downloaded until you
   agree.**
4. Agree, and the build starts in the background. Its progress is in the **Messages** panel as
   *package k of N*.

circuitRF runs the build inside Ubuntu, into `~/.circuitrf/solvers/palace/0.18.1/` in Ubuntu's own
filesystem. If a build tool is still missing, nothing is downloaded, and the message names the command
that installs it.

### 6. Check that it worked {#step-check}

When the build finishes, circuitRF checks the installed Palace before it says anything is done. The
Palace row in **Settings ▸ Solvers** then reads *Installed by circuitRF* and names the Ubuntu distribution
it found it in.

From here on you use Palace exactly as on any other computer. Simulate starts Ubuntu when it needs it,
runs Palace there, and brings the results back. You never need to open the Ubuntu window again. To try
it, open one of the examples in [3D EM ▸ Walking through the example](em-3d.html#example).

## Things that look wrong but are normal {#normal}

| What you see | Why |
|---|---|
| Nothing appears while typing a password | Linux hides passwords completely. Type it and press Enter. |
| Hundreds of lines scroll past during `apt-get` or the build | Each tool reports every file it fetches and compiles. Only the last lines matter if something fails, and circuitRF quotes those for you. |
| The build seems stuck on one package for a long time | Some of Palace's libraries take much longer to compile than others. The Messages panel moves on when that package is done. |
| A `vmmem` or `VmmemWSL` process uses a lot of memory in Task Manager | That is the Ubuntu virtual machine, building or running Palace. It goes away a minute or so after the last Linux program ends. |

## If a step fails {#problems}

| What happened | What fixes it |
|---|---|
| `apt-get install` fails with **404 Not Found** | Ubuntu's package list is older than the packages on the server. Run `sudo apt-get update`, then the install line again. The line in step 4 starts with that update for this reason. |
| `sudo` fails with **command not found**, or is not recognised as a command | It was typed at a Windows prompt (`C:\…>`). Type `wsl -d Ubuntu` first, or open Ubuntu from the Start menu, and run it there. See [step 4](#step-tools). |
| `wsl` is not recognised as a command | Windows is too old for `wsl --install`. Run Windows Update, then try step 2 again. |
| An error containing **0x80370102**, or a message about enabling virtualization in the BIOS | Virtualization is off. See [step 1](#step-virtualization). |
| circuitRF says the Linux subsystem is not enabled | Step 2 was not done, or Windows was not restarted after it. |
| circuitRF says there is no Linux distribution | Run `wsl --install -d Ubuntu` in an ordinary Terminal, then do step 3. |
| circuitRF says the distribution is WSL 1 | Run `wsl --set-version Ubuntu 2` in an ordinary Terminal. Everything in it is kept. |
| The build fails | circuitRF names the step that failed, quotes the tool's last error lines and gives the path of the full log. If it has seen that failure before, it says what fixed it. |
| A run warns that it may not fit in memory | WSL gets only part of your computer's memory by default. The warning names the setting that raises it, and [EM Setup ▸ Palace on Windows](em-setup.html#palace-windows) explains it. |

Every message circuitRF gives on the way, and what each missing piece looks like, is in
[EM Setup ▸ Palace on Windows](em-setup.html#palace-windows).

## Undoing all of it {#undo}

Each of these removes one layer. Stop at whichever you like.

1. **Palace.** **Uninstall Palace…** in **Settings ▸ Solvers** removes the folder circuitRF built inside
   Ubuntu. Ubuntu and the build tools stay.
2. **Ubuntu, and everything in it.** In an ordinary Terminal, run `wsl --unregister Ubuntu`. This
   deletes the Ubuntu virtual disk, including Palace, the build tools and any files you kept in your
   Linux home. It cannot be undone. Then uninstall **Ubuntu** from **Settings ▸ Apps** in Windows.
3. **WSL itself.** Uninstall **Windows Subsystem for Linux** from **Settings ▸ Apps**, then restart.

The virtualization setting from step 1 can stay on. Nothing uses it once WSL is gone.
