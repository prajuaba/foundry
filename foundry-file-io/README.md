# Foundry.FileIO

`Foundry.FileIO` is a high-performance, secure, and memory-efficient file processing library targeting C# (.NET 10). It provides pluggable streaming parsers (CSV, Excel), writers, and robust file security validations (magic bytes verification and path traversal sanitation).

---

## 🗺️ Key Features

*   **📥 Memory-Efficient Parsing (`IDataParser<T>`)**:
    *   **CSV Parsing (`CsvDataParser<T>`)**: Row-by-row parsing utilising `CsvHelper` under `IAsyncEnumerable<T>`.
    *   **Excel Parsing (`ExcelDataParser<T>`)**: Zero-allocation spreadsheet reading using `ExcelDataReader` with dynamic header reflection mapping, preventing high RAM overhead.
*   **📤 Direct Stream Exporting (`CsvDataExporter<T>`)**: Writes header records and iterates dataset collections directly to output streams on-the-fly, leaving streams open for further operations.
*   **🛡️ Signature & Magic Bytes Validator**: Inspects the actual binary headers (magic numbers) of uploaded files (PNG, JPG, PDF, ZIP, XLS, XLSX) to verify file integrity and prevent extension spoofing.
*   **🔀 Path Sanitation**: Strips parent directory access markers (`../`) and normalises directory separators to prevent **Arbitrary File Write / Path Traversal** vulnerabilities.

---

## 🛠️ Usage Examples

### 1. Parsing a CSV Upload Stream
```csharp
var parser = new CsvDataParser<InvoiceImportModel>();

await foreach (var invoice in parser.ParseAsync(uploadStream, cancellationToken))
{
    // Process each invoice row dynamically (Stage 2 business rules)
    await _invoiceService.ProcessInvoiceAsync(invoice);
}
```

### 2. Sanitising and Validating a File Upload
```csharp
var validator = new FileSecurityValidator();

// 1. Sanitise the name to exclude directory path modifiers
var cleanName = validator.SanitizeFileName(uploadedFile.FileName);

// 2. Inspect magic bytes to prevent spoofing
if (!validator.VerifySignature(cleanName, fileStream))
{
    throw new SecurityException("File header signature mismatch.");
}
```

### 3. Streaming a CSV Export Download
```csharp
var exporter = new CsvDataExporter<OrderModel>();

Response.ContentType = "text/csv";
Response.Headers.Add("Content-Disposition", "attachment; filename=orders.csv");

// Streams data straight from database out to HTTP response body without RAM buffers
await exporter.ExportAsync(
    _orderRepository.FindAllAsAsyncEnumerable(), 
    Response.Body, 
    cancellationToken
);
```
