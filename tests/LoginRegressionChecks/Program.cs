using System.Reflection;
using CarRentalAutomation.Infrastructure;

// These are synthetic numbers. No browser, database, network, or SMS is used.
var method = typeof(Yolcu360Browser).GetMethod("NormalizeTurkishPhoneNumber", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new InvalidOperationException("Production phone normalizer was not found.");
var valid = new[]
{
    "5551234567", "+90 555 123 45 67", "905551234567", "05551234567",
    "00905551234567", "+90 (555) 123-45-67", " 0555.123.45.67 "
};
var invalid = new string?[]
{
    null, "", " ", "555123456", "55512345678", "+49 5551234567",
    "495551234567", "90555123456", "9005551234567", "9055512345678",
    "4551234567", "0555123456", "555123456a", "555/123/4567", "++49 5551234567"
};
var count = 0;
foreach (var input in valid)
{
    var result = method.Invoke(null, [input]) as string;
    if (result != "5551234567") throw new InvalidOperationException("A valid phone format lost or duplicated digits.");
    count++;
}
foreach (var input in invalid)
{
    try
    {
        method.Invoke(null, [input]);
        throw new InvalidOperationException("An invalid phone format was accepted.");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is ArgumentException) { count++; }
}
using var script = typeof(Yolcu360Browser).Assembly.GetManifestResourceStream("CarRentalAutomation.Infrastructure.Yolcu360LoginPage.js")
    ?? throw new InvalidOperationException("Production login JavaScript was not embedded.");
using var reader = new StreamReader(script);
if (string.IsNullOrWhiteSpace(reader.ReadToEnd())) throw new InvalidOperationException("Embedded login JavaScript is empty.");
count++;
Console.WriteLine($"Passed {count} production phone/resource regression checks. No browser or SMS was started.");
