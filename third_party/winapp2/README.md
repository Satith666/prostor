# Winapp2

`cmd/prostor/rules/winapp2.json` is derived from [Winapp2.ini](https://github.com/MoscaDotTo/Winapp2)
by the Winapp2 project, licensed under [CC BY-SA 4.0](LICENSE.md).

Changes made by Prostor (`go run ./cmd/rulegen -in Winapp2.ini`):
only folders with caches, logs, temporary files and crash dumps are kept
(see `internal/rules/winapp2.go`); registry keys, entries touching cookies,
history, sessions, passwords, settings and personal folders are removed;
the result is converted to Prostor's rules format.

The derived file `cmd/prostor/rules/winapp2.json` is distributed under the same
CC BY-SA 4.0 license.
