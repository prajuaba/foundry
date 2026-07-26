using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using MongoDB.Bson;
using Foundry.FileIO;
using Foundry.E2E.Showcase.Entities;

namespace Foundry.E2E.Showcase.Services
{
    public record ProductImportModel
    {
        public string Sku { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public decimal UnitPrice { get; init; }
        public int StockQuantity { get; init; }
    }

    public record OrderExportModel
    {
        public string OrderNumber { get; init; } = string.Empty;
        public string CustomerId { get; init; } = string.Empty;
        public decimal TotalAmount { get; init; }
        public string Status { get; init; } = string.Empty;
        public string OrderDate { get; init; } = string.Empty;
    }

    public class CatalogFileService
    {
        private readonly CsvDataExporter<OrderExportModel> _exporter;
        private readonly CsvDataParser<ProductImportModel> _parser;
        private readonly FileSecurityValidator _securityValidator;

        public CatalogFileService()
        {
            _exporter = new CsvDataExporter<OrderExportModel>();
            _parser = new CsvDataParser<ProductImportModel>();
            _securityValidator = new FileSecurityValidator();
        }

        public async Task<string> ExportOrdersToCsvAsync(IEnumerable<Order> orders, string outputFileName)
        {
            var sanitizedFileName = _securityValidator.SanitizeFileName(outputFileName);
            var safePath = Path.Combine(AppContext.BaseDirectory, sanitizedFileName);

            var exportModels = new List<OrderExportModel>();
            foreach (var order in orders)
            {
                exportModels.Add(new OrderExportModel
                {
                    OrderNumber = order.OrderNumber,
                    CustomerId = order.CustomerId.ToString(),
                    TotalAmount = order.TotalAmount,
                    Status = order.Status.ToString(),
                    OrderDate = order.OrderDate.ToString("o")
                });
            }

            using var stream = File.Create(safePath);
            await _exporter.ExportAsync(ToAsyncEnumerable(exportModels), stream);
            return safePath;
        }

        public async Task<List<Product>> ImportProductsFromCsvAsync(byte[] fileBytes)
        {
            using var memoryStream = new MemoryStream(fileBytes);
            var products = new List<Product>();

            await foreach (var model in _parser.ParseAsync(memoryStream))
            {
                products.Add(new Product
                {
                    Id = ObjectId.GenerateNewId(),
                    Sku = model.Sku,
                    Name = model.Name,
                    UnitPrice = model.UnitPrice,
                    StockQuantity = model.StockQuantity
                });
            }

            return products;
        }

        private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> items)
        {
            foreach (var item in items)
            {
                yield return item;
                await Task.Yield();
            }
        }
    }
}
