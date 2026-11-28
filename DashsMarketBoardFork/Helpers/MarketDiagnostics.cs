namespace MarketBoardPlugin.Helpers
{
  using System;
  using System.Linq;
  using System.Text;
  using System.Text.Json;

  internal static class MarketDiagnostics
  {
    internal static string DescribeRawResponse(JsonElement root)
    {
      var sb = new StringBuilder();

      sb.AppendLine("=== Raw Universalis response ===");
      sb.AppendLine($"Item ID: {Read(root, "itemID")}");
      sb.AppendLine($"World: {Read(root, "worldName")}");
      sb.AppendLine($"World ID: {Read(root, "worldID")}");
      sb.AppendLine($"DC: {Read(root, "dcName")}");
      sb.AppendLine($"Region: {Read(root, "regionName")}");
      sb.AppendLine($"Last upload: {Read(root, "lastUploadTime")}");
      sb.AppendLine($"Reported listings: {Read(root, "listingsCount")}");
      sb.AppendLine($"Reported minimum price: {Read(root, "minPrice")}");
      sb.AppendLine($"World upload times: {Read(root, "worldUploadTimes")}");

      if (!root.TryGetProperty("listings", out var listings)
          || listings.ValueKind != JsonValueKind.Array)
      {
        sb.AppendLine("No listings array returned.");
        return sb.ToString();
      }

      sb.AppendLine($"Raw listing count: {listings.GetArrayLength()}");

      foreach (var group in listings.EnumerateArray()
          .GroupBy(x => ReadWorld(x, root))
          .OrderBy(x => x.Key))
      {
        var ordered = group.OrderBy(x => Price(x)).ToArray();

        sb.AppendLine(
            $"{group.Key}: {ordered.Length} listings, " +
            $"minimum {Read(ordered[0], "pricePerUnit")} gil");

        foreach (var listing in ordered.Take(5))
        {
          sb.AppendLine(
              $"  Unit={Read(listing, "pricePerUnit")}, " +
              $"Qty={Read(listing, "quantity")}, " +
              $"Total={Read(listing, "total")}, " +
              $"Tax={Read(listing, "tax")}, " +
              $"HQ={Read(listing, "hq")}");
        }
      }

      return sb.ToString();
    }

    private static string ReadWorld(JsonElement row, JsonElement root)
    {
      var name = Read(row, "worldName");
      var id = Read(row, "worldID");

      if (name == "-")
      {
        name = Read(root, "worldName");
      }

      if (id == "-")
      {
        id = Read(root, "worldID");
      }

      return $"{name} [ID {id}]";
    }

    private static long Price(JsonElement row)
    {
      return row.TryGetProperty("pricePerUnit", out var value)
          && value.ValueKind == JsonValueKind.Number
          && value.TryGetInt64(out var price)
          ? price
          : long.MaxValue;
    }

    private static string Read(JsonElement row, string property)
    {
      if (!row.TryGetProperty(property, out var value)
          || value.ValueKind == JsonValueKind.Null)
      {
        return "-";
      }

      return value.ValueKind == JsonValueKind.String
          ? value.GetString() ?? "-"
          : value.GetRawText();
    }
  }
}
