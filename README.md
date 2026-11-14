# Inventory & Sales

**[⬇ Download Inventory for Windows (InventorySetup.exe)](https://github.com/Kureijiotau/Inventory/releases/latest/download/InventorySetup.exe)** — run it and follow the wizard. Always the latest version.

An inventory and sales tracker for Windows 10 and 11, in English and Arabic.

- Stock by type, weight and size, with remaining quantity after sales
- Daily sales per customer, plus walk-in sales
- Customer payments, statements and total debts
- Data saved automatically to `Documents\Inventory`, with a copy kept for each of the last 30 days
- Tells you when a new version is available and updates with one click

## Use

Run the setup, then open **Inventory** from the desktop or Start menu. It needs Google Chrome or Microsoft Edge on the computer to show its window. No administrator password is needed.

Windows may show a "Windows protected your PC" warning the first time, because the program is not signed with a paid certificate. Choose **More info**, then **Run anyway**.

## Data

Everything is stored on the computer it is used on:

- `Documents\Inventory\data.json` — the live data
- `Documents\Inventory\backups\` — one copy per day, restorable from the Backup tab

Uninstalling the program never deletes this folder.

## Development

- `index.html` is the whole interface. Opened directly in a browser it still works, storing data in the browser instead of Documents.
- `host\Program.cs` is the Windows program: it shows `index.html` in a Chrome/Edge app window and reads and writes the data files.
- `.\build.ps1` builds `dist\Inventory.exe` using the C# compiler that ships with Windows.
- `installer.iss` is the Inno Setup script for the setup wizard.
- Every push to `main` builds and publishes a new version through `.github\workflows\package.yml`.

To test without touching real data, set the `INVENTORY_DATA_DIR` environment variable to another folder before starting `Inventory.exe`.
