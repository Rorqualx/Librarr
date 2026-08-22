# Security Policy

## Supported versions

Librarr is in beta, and only the most recent release is patched. That
is `1.2.2-beta` as of 2026-08-22 — earlier tags, `1.2.1-beta`,
`1.2.0-beta`, `1.1.0-beta` and `1.0.0-beta` included, get nothing. Check the [releases page][releases] rather than trusting
this line to have stayed current.

## Reporting a Vulnerability

Please report (suspected) security vulnerabilities by opening a
**private security advisory** on the GitHub repository:

<https://github.com/Rorqualx/Librarr/security/advisories/new>

Private vulnerability reporting was only switched on for this
repository on 2026-08-03. This file had pointed at that form since
2026-05-19, and for all of that time it was unreachable by anyone
without push access — so if you once tried to report something here
and could not, that is why, and it is worth trying again.

Use private advisories for issues that could compromise running
instances (auth bypass, RCE, SSRF, credential exposure, etc.). Public
issues are appropriate for everything else.

## What to expect

Librarr has one maintainer, who works on it in bursts that can be
weeks or months apart — see
[`docs/state-of-the-fork/`](docs/state-of-the-fork/) for the actual
observed cadence. This policy therefore does not promise a response
deadline, because a solo project can only commit to things one person
can deliver alone. Reports are read and acknowledged as soon as they
are seen; confirmed issues are patched as soon as severity and
complexity permit, and disclosure timing is agreed with the reporter.

What that means for you as a reporter: **if you have had no
acknowledgement after 14 days, treat the report as unseen rather than
declined, and consider yourself free to disclose** if the severity
warrants it. A live vulnerability sitting unread in a queue is worse
than an early disclosure, and you should not have to wait on one
person's availability to protect users.

This replaced a stated seven-day response commitment, which was
inherited boilerplate the project had no way to honour.

## Heritage

The upstream Readarr project used the Servarr Discord and
`development@servarr.com` for security reports. That channel is no
longer accepting Readarr reports; please use the GitHub advisory flow
above for Librarr-specific issues.

[releases]: https://github.com/Rorqualx/Librarr/releases
