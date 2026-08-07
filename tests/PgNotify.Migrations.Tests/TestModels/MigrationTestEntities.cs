namespace PgNotify.Migrations.Tests.TestModels;

public class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Sku { get; set; } = "";
    public decimal Price { get; set; }
}

public class OrderLine
{
    public int OrderId { get; set; }
    public int LineNumber { get; set; }
    public string Description { get; set; } = "";
}

public class Invoice
{
    public int Id { get; set; }
    public decimal Total { get; set; }
}

public class Document
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Metadata { get; set; } = "";
    public string Notes { get; set; } = "";
}
