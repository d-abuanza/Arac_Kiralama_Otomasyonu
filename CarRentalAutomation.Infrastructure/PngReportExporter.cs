using System.Drawing;
using System.Drawing.Imaging;
using CarRentalAutomation.Core;

namespace CarRentalAutomation.Infrastructure;

public sealed class PngReportExporter : IPngReportExporter
{
    private const int PageHeight = 1100;
    public void Export(SavedCollection collection, string path)
    {
        var pages = Math.Max(1, (int)Math.Ceiling(collection.Cars.Count / 25d));
        var width = pages > 1 ? 1400 : 1200;
        var height = pages == 1 ? Math.Max(500, 260 + collection.Cars.Count * 28) : PageHeight * pages;
        using var bitmap = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);
        using var titleFont = new Font("Segoe UI", 20, FontStyle.Bold);
        using var bodyFont = new Font("Segoe UI", 10);
        using var headerFont = new Font("Segoe UI", 10, FontStyle.Bold);
        using var brush = new SolidBrush(Color.FromArgb(35, 45, 60));
        graphics.DrawString("Araç Kiralama Teklif Raporu", titleFont, brush, 32, 24);
        graphics.DrawString($"Koleksiyon: {collection.Name}    Oluşturma: {DateTime.Now:dd.MM.yyyy HH:mm}", bodyFont, brush, 32, 64);
        graphics.DrawString($"Arama: {collection.Criteria.PickupLocation}, {collection.Criteria.PickupAt:dd.MM.yyyy HH:mm} – {collection.Criteria.ReturnAt:dd.MM.yyyy HH:mm}", bodyFont, brush, 32, 88);

        var headers = new[] { "Model", "Kiralama şirketi", "Vites", "Yakıt", "Fiyat" };
        var xs = new[] { 32, 340, 610, 800, 970 };
        for (var i = 0; i < collection.Cars.Count; i++)
        {
            var y = 140 + (i % 25) * 34 + (i / 25) * PageHeight;
            if (i % 25 == 0)
            {
                for (var h = 0; h < headers.Length; h++) graphics.DrawString(headers[h], headerFont, brush, xs[h], y);
                y += 28;
            }
            var c = collection.Cars[i];
            var row = new[] { c.Model, c.RentalCompany, c.Transmission, c.FuelType, $"{c.Price:N2} {c.Currency}" };
            for (var j = 0; j < row.Length; j++) graphics.DrawString(row[j], bodyFont, brush, xs[j], y + 28);
        }
        bitmap.Save(path, ImageFormat.Png);
    }
}
