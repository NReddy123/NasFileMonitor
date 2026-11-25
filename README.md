# NasFileMonitor (.NET 8 Windows Service Starter)

Production starter project for tracking and verifying files across on-premises NAS (SMB/NFS) paths using scheduled delta polling, a SQL Server State Ledger, regex archive matching, and Active/Passive lease failover.

## Quickstart

1. **Create the State Ledger schema** by running `sql/01_state_ledger_ddl.sql` against your ops SQL Server database.
2. **Create your local settings files** from the examples, then update `appsettings.json` with your `StateLedgerDb` and `BusinessAppDb` connection strings:
   ```powershell
   Copy-Item appsettings.example.json appsettings.json
   Copy-Item monitors.example.json monitors.json
   ```
   Register your parameterized SQL queries in `Verification/NamedQueryRegistry.cs`.
3. **Configure rules in `monitors.json`** (`FolderToFolder`, `FolderToDb`, or `Hybrid`).

   Local `appsettings.json` and `monitors.json` files are ignored by Git. Keep environment-specific connection strings and NAS paths out of committed files.
4. **Dry-run validate before deploying**:
   ```powershell
   dotnet run -- --validate-config
   ```
5. **Publish and install as a Windows Service** (running under a read-only gMSA):
   ```powershell
   dotnet publish -c Release -r win-x64 --self-contained false -o C:\Services\NasFileMonitor
   sc.exe create NasFileMonitorService binPath= "C:\Services\NasFileMonitor\NasFileMonitor.exe" obj= "DOMAIN\gMSA_NasMon$" start= auto
   sc.exe failure NasFileMonitorService reset= 86400 actions= restart/15000/restart/30000/restart/60000
   sc.exe start NasFileMonitorService
   ```
