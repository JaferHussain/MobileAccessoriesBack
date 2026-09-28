using System.Text;
using Dapper;
using MoizPos.Application.Abstractions;
using MoizPos.Domain.Entities;
using MoizPos.Domain.Enums;

namespace MoizPos.Infrastructure.Repositories;

/// <inheritdoc />
public sealed class ProductRepository : IProductRepository
{
    /// <summary>
    /// The price to quote. A wholesale sale is quoted wholesale_price; a walk-in is quoted
    /// retail_price. Resolved in SQL so the caller is handed one price — the one that applies —
    /// rather than both (FR-040: a Staff DTO may not carry a wholesale figure).
    ///
    /// <para>A product with no wholesale price set falls back to the retail price, so switching
    /// to wholesale can never quote zero for stock the owner has not priced for bulk yet.</para>
    ///
    /// <para>The shop has exactly three prices — cost, wholesale and retail. There was a fourth
    /// column, <c>sale_price</c>, which is what this used to read; migration 0029 removed it,
    /// because two columns for one price cannot be kept honest and the one that had to go was
    /// the one whose name did not say what it held.</para>
    /// </summary>
    private static string PriceColumn(SaleType saleType) =>
        saleType == SaleType.Wholesale
            ? "CASE WHEN p.wholesale_price > 0 THEN p.wholesale_price ELSE p.retail_price END"
            : "p.retail_price";

    /// <summary>
    /// The projected columns.
    ///
    /// <para><b>SalePrice is not a column.</b> It is whichever of the three stored prices applies
    /// to the sale being made, computed per request, so a caller receives one price rather than a
    /// menu of them. RetailPrice and WholesalePrice beside it ARE stored, and reach the Admin DTO
    /// only.</para>
    /// </summary>
    private static string SelectColumns(SaleType saleType) => $"""
        p.id                  AS Id,
        p.name                AS Name,
        p.category_id         AS CategoryId,
        c.name                AS Category,
        p.brand_id            AS BrandId,
        b.name                AS Brand,
        COALESCE(b.is_local, FALSE) AS BrandIsLocal,
        p.model               AS Model,
        p.barcode             AS Barcode,
        p.image_path          AS ImagePath,
        p.cost_price          AS CostPrice,
        p.wholesale_price     AS WholesalePrice,
        p.retail_price        AS RetailPrice,
        {PriceColumn(saleType)} AS SalePrice,
        p.quantity_on_hand    AS QuantityOnHand,
        p.min_stock_threshold AS MinStockThreshold,
        p.supplier_id         AS SupplierId,
        s.name                AS SupplierName,
        p.is_active           AS IsActive
        """;

    private readonly IDbConnectionFactory _connectionFactory;

    public ProductRepository(IDbConnectionFactory connectionFactory) =>
        _connectionFactory = connectionFactory;

    public async Task<(IReadOnlyList<ProductRow> Items, int TotalItems)> SearchAsync(
        ProductQuery query,
        CancellationToken cancellationToken = default)
    {
        var where = new StringBuilder("WHERE 1 = 1");
        var parameters = new DynamicParameters();

        if (!query.IncludeInactive)
        {
            where.Append(" AND p.is_active = TRUE");
        }

        if (query.SearchWords.Count > 0)
        {
            // Word-by-word search (FR-074 … FR-080). Each word must appear in at least one of the
            // product's name, model, brand or category; every word must match.
            //
            // Each field is normalised ON ITS OWN — lower-cased, everything but letters and digits
            // removed — so "Type-C Braided Cable" becomes "typecbraidedcable" and "typec" matches.
            // Fields are never concatenated first: that would let a word match across the boundary
            // between two of them ("leba" in "cabLE" + "BAseus").
            //
            // LIKE rather than MATCH...AGAINST: fulltext tokenises on word boundaries, so "typec"
            // could never match "Type-C", and InnoDB's minimum token length drops the "c" from
            // "c type". A 5,000-row catalogue is small enough to scan (research R4).
            //
            // The words arrive already reduced to [a-z0-9] by ProductSearchTerms and are still
            // bound as parameters — never concatenated into the SQL.
            var matches = new List<string>(query.SearchWords.Count);

            for (var i = 0; i < query.SearchWords.Count; i++)
            {
                var name = $"word{i}";

                matches.Add($"""
                    (REGEXP_REPLACE(LOWER(p.name),  '[^a-z0-9]', '') LIKE @{name}
                  OR REGEXP_REPLACE(LOWER(p.model), '[^a-z0-9]', '') LIKE @{name}
                  OR REGEXP_REPLACE(LOWER(b.name),  '[^a-z0-9]', '') LIKE @{name}
                  OR REGEXP_REPLACE(LOWER(c.name),  '[^a-z0-9]', '') LIKE @{name})
                """);

                parameters.Add(name, $"%{query.SearchWords[i]}%");
            }

            // A scanned barcode is matched exactly and whole, as it always was (FR-080), as an
            // alternative to the word match.
            where.Append(" AND ((").Append(string.Join(" AND ", matches)).Append(") OR p.barcode = @exactSearch)");
            parameters.Add("exactSearch", query.Search ?? string.Empty);
        }

        if (query.CategoryId.HasValue)
        {
            where.Append(" AND p.category_id = @categoryId");
            parameters.Add("categoryId", query.CategoryId.Value);
        }

        if (query.BrandId.HasValue)
        {
            where.Append(" AND p.brand_id = @brandId");
            parameters.Add("brandId", query.BrandId.Value);
        }

        if (query.LocalOnly)
        {
            // Through the LEFT JOIN, an unbranded product has b.is_local NULL, which is not TRUE —
            // so it is excluded here with no special case (data-model §5).
            where.Append(" AND b.is_local = TRUE");
        }

        if (query.LowStockOnly)
        {
            where.Append(" AND p.quantity_on_hand <= p.min_stock_threshold");
        }

        parameters.Add("offset", (query.Page - 1) * query.PageSize);
        parameters.Add("limit", query.PageSize);

        var sql = $"""
            SELECT {SelectColumns(query.SaleType)}
            FROM products p
            JOIN categories c ON c.id = p.category_id
            LEFT JOIN brands b ON b.id = p.brand_id
            LEFT JOIN suppliers s ON s.id = p.supplier_id
            {where}
            ORDER BY p.name, p.id
            LIMIT @limit OFFSET @offset;

            SELECT COUNT(*)
            FROM products p
            JOIN categories c ON c.id = p.category_id
            LEFT JOIN brands b ON b.id = p.brand_id
            {where};
            """;

        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var reader = await connection.QueryMultipleAsync(sql, parameters);

        var items = (await reader.ReadAsync<ProductRow>()).AsList();
        var total = await reader.ReadSingleAsync<int>();

        return (items, total);
    }

    public async Task<ProductRow?> FindByIdAsync(
        long id,
        SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<ProductRow>(
            $"""
             SELECT {SelectColumns(saleType)}
             FROM products p
             JOIN categories c ON c.id = p.category_id
             LEFT JOIN brands b ON b.id = p.brand_id
             LEFT JOIN suppliers s ON s.id = p.supplier_id
             WHERE p.id = @id
             LIMIT 1;
             """,
            new { id });
    }

    public async Task<ProductRow?> FindByBarcodeAsync(
        string barcode,
        SaleType saleType = SaleType.Retail,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.QuerySingleOrDefaultAsync<ProductRow>(
            $"""
             SELECT {SelectColumns(saleType)}
             FROM products p
             JOIN categories c ON c.id = p.category_id
             LEFT JOIN brands b ON b.id = p.brand_id
             LEFT JOIN suppliers s ON s.id = p.supplier_id
             WHERE p.barcode = @barcode AND p.is_active = TRUE
             LIMIT 1;
             """,
            new { barcode });
    }

    public async Task<long> CreateAsync(Product product, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<long>(
            """
            INSERT INTO products
                (name, category_id, brand_id, model, barcode, image_path, cost_price, wholesale_price,
                 retail_price, quantity_on_hand, min_stock_threshold, supplier_id,
                 is_active, created_at_utc)
            VALUES
                (@Name, @CategoryId, @BrandId, @Model, @Barcode, @ImagePath, @CostPrice, @WholesalePrice,
                 @RetailPrice, @QuantityOnHand, @MinStockThreshold, @SupplierId,
                 TRUE, UTC_TIMESTAMP(6));
            SELECT LAST_INSERT_ID();
            """,
            product);
    }

    public async Task UpdateAsync(Product product, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Deliberately does NOT touch quantity_on_hand or cost_price: stock moves only through
        // purchases, sales, returns and audited adjustments, and cost moves only through a
        // purchase (FR-011a). Editing a product must never be a back door into either.
        await connection.ExecuteAsync(
            """
            UPDATE products
            SET name = @Name,
                category_id = @CategoryId,
                brand_id = @BrandId,
                model = @Model,
                barcode = @Barcode,
                -- No prices here. They are the purchase's to set, so that changing what a
                -- product IS can never quietly change what it is worth.
                min_stock_threshold = @MinStockThreshold,
                supplier_id = @SupplierId,
                updated_at_utc = UTC_TIMESTAMP(6)
            WHERE id = @Id;
            """,
            product);
    }

    public async Task DeactivateAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        // Never hard-deleted: historical invoices reference this row (FR-002).
        await connection.ExecuteAsync(
            "UPDATE products SET is_active = FALSE, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @id;",
            new { id });
    }

    public async Task SetImagePathAsync(
        long id,
        string imagePath,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        await connection.ExecuteAsync(
            "UPDATE products SET image_path = @imagePath, updated_at_utc = UTC_TIMESTAMP(6) WHERE id = @id;",
            new { id, imagePath });
    }

    public async Task<bool> BarcodeExistsAsync(
        string barcode,
        long? excludingProductId = null,
        CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);

        return await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*) FROM products
            WHERE barcode = @barcode AND (@excludingProductId IS NULL OR id <> @excludingProductId);
            """,
            new { barcode, excludingProductId }) > 0;
    }
}
