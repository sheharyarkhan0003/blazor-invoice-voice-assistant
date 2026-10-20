using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace SimpleInvoiceManagementSoftware
{
    /// <summary>One thing the user asked for, as understood by the local AI model.</summary>
    public class VoiceAction
    {
        public const string AddProduct = "add_product";
        public const string UpdatePrice = "update_price";
        public const string AddCustomer = "add_customer";
        public const string CreateInvoice = "create_invoice";
        public const string Unknown = "unknown";

        [JsonPropertyName("type")] public string Type { get; set; }

        /// <summary>add_product: new product name. update_price: existing product name. add_customer: customer name.</summary>
        [JsonPropertyName("name")] public string Name { get; set; }

        // add_product / update_price
        [JsonPropertyName("size")] public string Size { get; set; }
        [JsonPropertyName("price")] public double? Price { get; set; }

        // create_invoice
        [JsonPropertyName("customer")] public string Customer { get; set; }
        [JsonPropertyName("items")] public List<VoiceInvoiceItem> Items { get; set; }

        // add_customer
        [JsonPropertyName("address")] public string Address { get; set; }
        [JsonPropertyName("city")] public string City { get; set; }

        // unknown
        [JsonPropertyName("reason")] public string Reason { get; set; }
    }

    public class VoiceInvoiceItem
    {
        [JsonPropertyName("product")] public string Product { get; set; }
        [JsonPropertyName("quantity")] public double Quantity { get; set; }
    }

    /// <summary>
    /// Understands spoken commands with a local, open-source LLM served by Ollama (http://localhost:11434).
    /// Nothing leaves the PC and no paid API is used. The model only proposes actions; the caller validates them.
    /// </summary>
    public class LocalAiService
    {
        // The instructions and examples stay the same on every call, so Ollama can reuse its cache for them.
        private const string SystemPrompt = @"You convert a shop owner's spoken command into actions for an invoicing app.
The text comes from speech-to-text: brand names can be misspelled, Hindi/Urdu words can be mixed in, numbers can be words, and ""for"" can appear as ""4"". Prices are in Indian rupees.

Reply with compact JSON on one line (no newlines or indentation).
Every action always has type, name, size and price (use """" or 0 when they do not apply). Add customer and items only for create_invoice, address and city only for add_customer, and reason only for unknown.
- type: add_product | update_price | add_customer | create_invoice | unknown
- name: add_product -> the new product's name in Title Case, brand spelling fixed, without the size. update_price -> the matching name from EXISTING PRODUCTS. add_customer -> the customer or business name.
- size: add_product only. Pack size with a short unit: ""1 kg"", ""500 g"", ""1 litre"", ""200 ml"", ""12 pcs"". """" if no size was said.
- price: add_product -> its price. update_price -> the new price. Never invent a price: 0 if none was said. A weight or size is never a price.
- customer: create_invoice only -> the matching name from EXISTING CUSTOMERS.
- items: create_invoice only -> [{product, quantity}] with product = the matching name from EXISTING PRODUCTS, quantity 1 if not said.
- address, city: add_customer only.
- reason: unknown only -> a short reason.

Rules:
- Any wording that means a new product is add_product: ""add"", ""put ... in"", ""new item"", ""add kar do"", ""daal do"".
- ""X is now N"", ""make X N rupees"", ""increase/decrease/change the price of X to N"" is update_price.
- ""invoice"", ""bill"", ""make a bill for"" is create_invoice.
- Fix brand names that speech-to-text misheard, e.g. ""Surfix Cell"" -> ""Surf Excel"", ""Colget"" -> ""Colgate"", ""Dairy Milk"" stays ""Dairy Milk"".
- ""N rupees ka packet"" or ""N rupees per packet"" means price N for one packet: size """", not ""N pcs"".
- Sizes: ""half kg"" = ""500 g"", ""quarter kg"" = ""250 g"", ""one kilo"" = ""1 kg"". When matching EXISTING PRODUCTS, the spoken size must match the product's size; ""one half kg Surf Excel"" is quantity 1 of the 500 g product.
- One sentence can hold several actions. Keep the spoken order, and give each product its own price.
- Greetings, thanks, chit-chat or noise -> one unknown action.

Examples, with EXISTING PRODUCTS ""Surf Excel 1 kg"", ""Tata Salt 1 kg"" and EXISTING CUSTOMERS ""Ali Traders"":
""Put Pepsi in, the one litre bottle, it's sixty five rupees"" -> {""actions"":[{""type"":""add_product"",""name"":""Pepsi"",""size"":""1 litre"",""price"":65}]}
""Bhai Parle G add kar do, 10 rupees ka packet"" -> {""actions"":[{""type"":""add_product"",""name"":""Parle-G"",""size"":"""",""price"":10}]}
""Add Vim bar 200 grams for 20 and Harpic 500 ml for 95"" -> {""actions"":[{""type"":""add_product"",""name"":""Vim Bar"",""size"":""200 g"",""price"":20},{""type"":""add_product"",""name"":""Harpic"",""size"":""500 ml"",""price"":95}]}
""Salt is now 30 rupees"" -> {""actions"":[{""type"":""update_price"",""name"":""Tata Salt 1 kg"",""size"":"""",""price"":30}]}
""Bill for Ali Traders, 2 Surf Excel and 5 salt"" -> {""actions"":[{""type"":""create_invoice"",""name"":"""",""size"":"""",""price"":0,""customer"":""Ali Traders"",""items"":[{""product"":""Surf Excel 1 kg"",""quantity"":2},{""product"":""Tata Salt 1 kg"",""quantity"":5}]}]}
""New customer Bilal Stores, Liberty Market, Lahore"" -> {""actions"":[{""type"":""add_customer"",""name"":""Bilal Stores"",""size"":"""",""price"":0,""address"":""Liberty Market"",""city"":""Lahore""}]}
""Okay, thank you"" -> {""actions"":[{""type"":""unknown"",""name"":"""",""size"":"""",""price"":0,""reason"":""chit-chat""}]}";

        private static readonly object Schema = new
        {
            type = "object",
            properties = new
            {
                actions = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        // name/size/price are required: small models otherwise skip them (e.g. the new price of update_price).
                        properties = new Dictionary<string, object>
                        {
                            ["type"] = new { type = "string", @enum = new[] { VoiceAction.AddProduct, VoiceAction.UpdatePrice, VoiceAction.AddCustomer, VoiceAction.CreateInvoice, VoiceAction.Unknown } },
                            ["name"] = new { type = "string" },
                            ["size"] = new { type = "string" },
                            ["price"] = new { type = "number" },
                            ["customer"] = new { type = "string" },
                            ["items"] = new
                            {
                                type = "array",
                                items = new
                                {
                                    type = "object",
                                    properties = new { product = new { type = "string" }, quantity = new { type = "number" } },
                                    required = new[] { "product", "quantity" }
                                }
                            },
                            ["address"] = new { type = "string" },
                            ["city"] = new { type = "string" },
                            ["reason"] = new { type = "string" },
                        },
                        required = new[] { "type", "name", "size", "price" }
                    }
                }
            },
            required = new[] { "actions" }
        };

        private readonly HttpClient http;
        private readonly string model;

        public LocalAiService(HttpClient http, IConfiguration configuration)
        {
            this.http = http;
            this.http.BaseAddress = new Uri(configuration["Ai:BaseUrl"] ?? "http://localhost:11434");
            this.http.Timeout = Timeout.InfiniteTimeSpan;   // a first-time model download can take many minutes
            model = configuration["Ai:Model"] ?? "qwen3:4b";
        }

        public string ModelName => model;

        /// <summary>Checks that Ollama is running, downloads the model if needed, and loads it into memory.</summary>
        public async Task WarmUpAsync(Action<string> reportStatus, CancellationToken cancellationToken = default)
        {
            HttpResponseMessage tags;
            try
            {
                tags = await http.GetAsync("api/tags", cancellationToken);
            }
            catch (HttpRequestException)
            {
                throw new InvalidOperationException("Ollama is not running. Start \"Ollama\" from the Windows Start menu, then try again.");
            }

            using (var doc = JsonDocument.Parse(await tags.Content.ReadAsStringAsync(cancellationToken)))
            {
                var installed = doc.RootElement.GetProperty("models").EnumerateArray()
                    .Select(m => m.GetProperty("name").GetString())
                    .Any(name => name == model || name == model + ":latest");
                if (!installed)
                {
                    reportStatus($"Downloading the AI model {model}... The first time, this is about 2.5 GB.");
                    var pull = await http.PostAsJsonAsync("api/pull", new { model, stream = false }, cancellationToken);
                    pull.EnsureSuccessStatusCode();
                }
            }

            // A tiny request with the real instructions: loads the model and caches the instruction tokens,
            // so the first spoken command does not pay for reading them.
            reportStatus($"Loading the AI model {model}...");
            var load = await http.PostAsJsonAsync("api/chat", ChatRequest("EXISTING PRODUCTS:", 1), cancellationToken);
            load.EnsureSuccessStatusCode();
        }

        // Warm-up and real requests must send the same options and system prompt: another num_ctx makes
        // Ollama reload the model, and an identical system prompt lets it reuse the cached prompt tokens.
        private object ChatRequest(string userContent, int maxTokens) => new
        {
            model,
            stream = false,
            think = false,
            keep_alive = "30m",
            format = Schema,
            options = new { temperature = 0, num_ctx = 8192, num_predict = maxTokens },   // room for the instructions plus long product lists
            messages = new[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userContent },
            }
        };

        public async Task<List<VoiceAction>> InterpretAsync(string spokenText, IEnumerable<string> products, IEnumerable<string> customers, CancellationToken cancellationToken = default)
        {
            var user = new StringBuilder();
            user.AppendLine("EXISTING PRODUCTS:");
            foreach (var p in products) user.AppendLine("- " + p);
            user.AppendLine();
            user.AppendLine("EXISTING CUSTOMERS:");
            foreach (var c in customers) user.AppendLine("- " + c);
            user.AppendLine();
            user.AppendLine("SPOKEN COMMAND:");
            user.Append('"').Append(spokenText).Append('"');

            var response = await http.PostAsJsonAsync("api/chat", ChatRequest(user.ToString(), 1024), cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The AI model returned an error: {body}");
            }

            using (var doc = JsonDocument.Parse(body))
            {
                var content = doc.RootElement.GetProperty("message").GetProperty("content").GetString();
                using (var result = JsonDocument.Parse(content))
                {
                    return JsonSerializer.Deserialize<List<VoiceAction>>(result.RootElement.GetProperty("actions").GetRawText()) ?? new List<VoiceAction>();
                }
            }
        }
    }
}
