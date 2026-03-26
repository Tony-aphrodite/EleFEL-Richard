using System.Runtime.InteropServices;
using System.Text;
using EleFEL.Core.Models;

namespace EleFEL.Core.Services;

/// <summary>
/// ESC/POS thermal printer service for printing invoices.
/// Supports 58mm and 80mm thermal printers via direct port writing.
/// </summary>
public class ThermalPrinterService
{
    private readonly PrinterConfig _config;
    private readonly LogService _log;

    // ESC/POS Commands
    private static readonly byte[] CMD_INIT = { 0x1B, 0x40 };                    // Initialize
    private static readonly byte[] CMD_ALIGN_CENTER = { 0x1B, 0x61, 0x01 };      // Center align
    private static readonly byte[] CMD_ALIGN_LEFT = { 0x1B, 0x61, 0x00 };        // Left align
    private static readonly byte[] CMD_ALIGN_RIGHT = { 0x1B, 0x61, 0x02 };       // Right align
    private static readonly byte[] CMD_BOLD_ON = { 0x1B, 0x45, 0x01 };           // Bold on
    private static readonly byte[] CMD_BOLD_OFF = { 0x1B, 0x45, 0x00 };          // Bold off
    private static readonly byte[] CMD_DOUBLE_HEIGHT = { 0x1B, 0x21, 0x10 };     // Double height
    private static readonly byte[] CMD_NORMAL_SIZE = { 0x1B, 0x21, 0x00 };       // Normal size
    private static readonly byte[] CMD_CUT = { 0x1D, 0x56, 0x41, 0x03 };         // Partial cut
    private static readonly byte[] CMD_FEED_3 = { 0x1B, 0x64, 0x03 };            // Feed 3 lines

    public ThermalPrinterService(PrinterConfig config, LogService log)
    {
        _config = config;
        _log = log;
    }

    /// <summary>
    /// Prints an invoice receipt on the thermal printer
    /// </summary>
    public async Task<bool> PrintInvoiceAsync(Invoice invoice, EleventaSale sale, EmitterConfig emitter)
    {
        if (!_config.Enabled)
        {
            _log.LogInfo("Printer disabled, skipping print");
            return true;
        }

        try
        {
            var data = BuildReceiptData(invoice, sale, emitter);
            await SendToPrinterAsync(data);
            _log.LogInfo($"Invoice printed: SaleID={invoice.EleventaSaleId}");
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError($"Print failed for SaleID={invoice.EleventaSaleId}", ex);
            return false;
        }
    }

    private byte[] BuildReceiptData(Invoice invoice, EleventaSale sale, EmitterConfig emitter)
    {
        using var ms = new MemoryStream();
        var encoding = Encoding.GetEncoding("IBM850");
        int lineWidth = _config.PaperWidthMm >= 80 ? 48 : 32;

        void Write(byte[] bytes) => ms.Write(bytes, 0, bytes.Length);
        void WriteText(string text) { var b = encoding.GetBytes(text + "\n"); ms.Write(b, 0, b.Length); }
        void WriteLine() => WriteText(new string('-', lineWidth));

        // Initialize
        Write(CMD_INIT);

        // Header - Emitter info
        Write(CMD_ALIGN_CENTER);
        Write(CMD_BOLD_ON);
        Write(CMD_DOUBLE_HEIGHT);
        WriteText(emitter.CommercialName);
        Write(CMD_NORMAL_SIZE);
        Write(CMD_BOLD_OFF);
        WriteText(emitter.Name);
        WriteText($"NIT: {emitter.Nit}");
        WriteText(emitter.Address);
        WriteText($"{emitter.Municipality}, {emitter.Department}");

        WriteLine();

        // DTE Info
        Write(CMD_BOLD_ON);
        WriteText("DOCUMENTO TRIBUTARIO ELECTRONICO");
        WriteText("FACTURA ELECTRONICA");
        Write(CMD_BOLD_OFF);

        WriteLine();

        Write(CMD_ALIGN_LEFT);
        WriteText($"UUID: {invoice.Uuid ?? "Pendiente"}");
        WriteText($"Serie: {invoice.SerialNumber ?? "-"}");
        WriteText($"Numero: {invoice.DteNumber ?? "-"}");
        WriteText($"Fecha: {invoice.CertificationDate?.ToString("dd/MM/yyyy HH:mm") ?? "-"}");

        WriteLine();

        // Customer
        WriteText($"NIT: {invoice.CustomerNit}");
        WriteText($"Nombre: {invoice.CustomerName}");

        WriteLine();

        // Items header
        Write(CMD_BOLD_ON);
        WriteText(FormatLine("Descripcion", "Total", lineWidth));
        Write(CMD_BOLD_OFF);

        foreach (var item in sale.Items)
        {
            WriteText(item.Description.Length > lineWidth - 10
                ? item.Description[..(lineWidth - 10)]
                : item.Description);
            WriteText(FormatLine(
                $"  {item.Quantity:F2} x Q{item.UnitPrice:F2}",
                $"Q{item.LineTotal:F2}",
                lineWidth));
        }

        WriteLine();

        // Total
        Write(CMD_BOLD_ON);
        Write(CMD_DOUBLE_HEIGHT);
        WriteText(FormatLine("TOTAL:", $"Q{invoice.Total:N2}", lineWidth));
        Write(CMD_NORMAL_SIZE);
        Write(CMD_BOLD_OFF);

        WriteLine();

        // Footer
        Write(CMD_ALIGN_CENTER);
        WriteText("Certificador: Infile, S.A.");
        WriteText("NIT Certificador: 12521337");
        WriteText("");
        WriteText("Consulte su factura en:");
        WriteText("https://report.feel.com.gt");
        WriteText($"UUID: {invoice.Uuid ?? ""}");

        // Feed and cut
        Write(CMD_FEED_3);
        Write(CMD_CUT);

        return ms.ToArray();
    }

    private Task SendToPrinterAsync(byte[] data)
    {
        var printerName = _config.PrinterName;

        if (string.IsNullOrEmpty(printerName))
            throw new InvalidOperationException("Printer name not configured");

        if (!RawPrinterHelper.SendBytesToPrinter(printerName, data))
            throw new InvalidOperationException($"Failed to send data to printer: {printerName}");

        return Task.CompletedTask;
    }

    /// <summary>
    /// Windows API raw printing helper - sends raw bytes directly to a printer
    /// </summary>
    private static class RawPrinterHelper
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        private class DOCINFOA
        {
            [MarshalAs(UnmanagedType.LPStr)] public string? pDocName;
            [MarshalAs(UnmanagedType.LPStr)] public string? pOutputFile;
            [MarshalAs(UnmanagedType.LPStr)] public string? pDataType;
        }

        [DllImport("winspool.Drv", EntryPoint = "OpenPrinterA", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern bool OpenPrinter([MarshalAs(UnmanagedType.LPStr)] string szPrinter, out IntPtr hPrinter, IntPtr pd);

        [DllImport("winspool.Drv", EntryPoint = "ClosePrinter", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "StartDocPrinterA", CharSet = CharSet.Ansi, SetLastError = true)]
        private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFOA di);

        [DllImport("winspool.Drv", EntryPoint = "EndDocPrinter", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "StartPagePrinter", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "EndPagePrinter", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr hPrinter);

        [DllImport("winspool.Drv", EntryPoint = "WritePrinter", SetLastError = true)]
        private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

        public static bool SendBytesToPrinter(string printerName, byte[] data)
        {
            var di = new DOCINFOA { pDocName = "EleFEL Receipt", pDataType = "RAW" };
            IntPtr hPrinter = IntPtr.Zero;

            if (!OpenPrinter(printerName.Normalize(), out hPrinter, IntPtr.Zero))
                return false;

            try
            {
                if (!StartDocPrinter(hPrinter, 1, di)) return false;
                if (!StartPagePrinter(hPrinter)) return false;

                var pUnmanagedBytes = Marshal.AllocCoTaskMem(data.Length);
                try
                {
                    Marshal.Copy(data, 0, pUnmanagedBytes, data.Length);
                    if (!WritePrinter(hPrinter, pUnmanagedBytes, data.Length, out _))
                        return false;
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pUnmanagedBytes);
                }

                EndPagePrinter(hPrinter);
                EndDocPrinter(hPrinter);
                return true;
            }
            finally
            {
                ClosePrinter(hPrinter);
            }
        }
    }

    private static string FormatLine(string left, string right, int width)
    {
        int spaces = width - left.Length - right.Length;
        if (spaces < 1) spaces = 1;
        return left + new string(' ', spaces) + right;
    }
}
