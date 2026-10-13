# Contributing to RemoteHub

Thank you for helping! Bug reports, ideas and pull requests are welcome.

- **Security problems:** please don't open an issue – report them privately as described in [SECURITY.md](SECURITY.md).
- **Bugs and ideas:** open an issue with what you did, what you expected and what happened (version, Windows version).
- **Pull requests:** for anything larger than a small fix, open an issue first, so we can agree on the approach before
  you spend time on it.

## Before you open a pull request

- Build and test: `dotnet build RemoteHub.sln` without warnings, and `dotnet test tests/RemoteHub.Tests` passing.
- Follow the code around your change: naming, comments, how the existing pages and dialogs are built.
- Keep a pull request to one topic, and describe what it changes and how you tested it.
- Don't include code you didn't write without saying where it comes from and under which license.

## Contributor License Agreement

Before your first pull request can be merged, you need to agree to the
[RemoteHub Contributor License Agreement](CLA.md). The CLA Assistant bot asks you in the pull request; you agree by
commenting:

> I have read the RemoteHub CLA and I agree to it

You only do this once; it covers all your contributions.

**Why a CLA?** RemoteHub is licensed under the [GNU AGPL-3.0](LICENSE). With the CLA you keep the copyright to your
work, but you allow the project to license it under other terms as well – for example a commercial license for
companies that can't use AGPL software. Without it, the project couldn't offer such a license for code that includes
your contribution. Everything published under the AGPL stays available under it.

## License

By contributing, you agree that your contributions are licensed under the AGPL-3.0 as part of RemoteHub, and under the
terms of the [CLA](CLA.md).
