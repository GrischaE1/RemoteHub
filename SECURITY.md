# Security policy

RemoteHub stores credentials for servers, and the RemoteHub Agent runs as LocalSystem and gives remote access to the
devices it is installed on. Security problems in either are taken seriously – thank you for reporting them privately.

## Reporting a vulnerability

**Please don't open a public issue, pull request or discussion for a security problem.**

Report it privately through GitHub: **[Report a vulnerability](https://github.com/GrischaE1/RemoteHub/security/advisories/new)**
(the *Security* tab of this repository → *Report a vulnerability*). Only the maintainers see the report.

Helpful to include:

- the affected component (RemoteHub, the RemoteHub Agent, the installer, the update check) and version;
- what an attacker needs (network access, a local account, an admin account on RemoteHub's PC …) and what they gain;
- steps to reproduce, or a proof of concept;
- the Windows version and anything unusual about the setup.

## What happens next

- You get an answer as soon as possible – the aim is within a week.
- The problem is confirmed or explained, and fixed in a new release. You are kept informed along the way.
- Once a fixed version is out, the issue is published as a GitHub security advisory. You are credited, unless you
  prefer not to be.

Please give us a reasonable time to release a fix before you publish anything yourself.

## Supported versions

RemoteHub is before 1.0: security fixes go into the **latest release** only. Please check that the problem still
happens with it.

| Version | Security fixes |
|---|---|
| Latest release | ✅ |
| Older releases | ❌ – update to the latest release |

## Scope

In scope – for example:

- the agent's sign-in, rights and policies, consent (Ask / PIN) and privacy mode – anything that lets someone connect,
  see or control more than they should, or see what a user hid;
- the stored credentials and the encrypted settings database, backups and exports;
- the update check and installer (anything that gets other code installed or run);
- the anonymous usage data sending more than [PRIVACY.md](PRIVACY.md) says.

Out of scope:

- vulnerabilities in third-party components ([THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)) – please report those
  to their projects (tell us too if RemoteHub is affected);
- the test lab scripts in `lab\` (they run on a private Hyper-V host, not on users' machines);
- attacks that need an administrator or LocalSystem account on the device already – they control the device anyway;
- denial of service through floods of network traffic.
