using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Radzen;
using SimpleInvoiceManagementSoftware.Models.SimpleInvoice;

namespace SimpleInvoiceManagementSoftware
{
    /// <summary>The screen the voice assistant is on; each screen does its own kind of work.</summary>
    public enum VoiceScreen
    {
        Invoices,
        Products,
        Customers
    }

    public class VoiceActionResult
    {
        public NotificationSeverity Kind { get; set; }
        public string Title { get; set; }
        public string Message { get; set; }
        public List<string> Details { get; set; } = new List<string>();

        /// <summary>Optional page to open, e.g. the record that was created.</summary>
        public string Link { get; set; }
        public string LinkText { get; set; }
    }

    /// <summary>
    /// Runs the actions the local AI understood from a spoken command. The AI output is never trusted
    /// directly: names are matched against the database here, and anything unclear is reported back
    /// with a tip on what to say instead.
    /// </summary>
    public class VoiceCommandExecutor
    {
        private static readonly Dictionary<string, (VoiceScreen Screen, string Page)> HomeScreen = new Dictionary<string, (VoiceScreen, string)>
        {
            [VoiceAction.AddProduct] = (VoiceScreen.Products, "products"),
            [VoiceAction.UpdatePrice] = (VoiceScreen.Products, "products"),
            [VoiceAction.AddCustomer] = (VoiceScreen.Customers, "customers"),
            [VoiceAction.CreateInvoice] = (VoiceScreen.Invoices, "invoices"),
        };

        private readonly SimpleInvoiceService simpleInvoice;
        private readonly LocalAiService ai;

        public VoiceCommandExecutor(SimpleInvoiceService simpleInvoice, LocalAiService ai)
        {
            this.simpleInvoice = simpleInvoice;
            this.ai = ai;
        }

        public static string[] ExamplesFor(VoiceScreen screen)
        {
            switch (screen)
            {
                case VoiceScreen.Products:
                    return new[] { "Add Surf Excel 1 kg for 500 rupees", "Put Colgate in, the 200 gram one, it's 99", "Tata Salt is now 28 rupees" };
                case VoiceScreen.Customers:
                    return new[] { "New customer Ali Traders, Mall Road, Lahore", "Add customer Ahmed General Store in Karachi" };
                default:
                    return new[] { "Make an invoice for Ali Traders with 2 Surf Excel and 5 Tata Salt", "Bill for Default Customer, 3 Colgate" };
            }
        }

        /// <summary>Product and customer names, used to help both Whisper and the AI recognise them.</summary>
        public async Task<(List<Product> Products, List<Customer> Customers)> LoadKnownNamesAsync()
        {
            var products = await (await simpleInvoice.GetProducts()).AsNoTracking().OrderBy(p => p.Name).Take(300).ToListAsync();
            var customers = await (await simpleInvoice.GetCustomers()).AsNoTracking().OrderBy(c => c.Name).Take(300).ToListAsync();
            return (products, customers);
        }

        /// <summary>Step 1: ask the local AI what the user meant. Nothing is saved yet.</summary>
        public async Task<List<VoiceAction>> UnderstandAsync(string spokenText)
        {
            var (products, customers) = await LoadKnownNamesAsync();
            return await ai.InterpretAsync(
                spokenText,
                products.Select(p => $"{p.Name} (₹{p.UnitPrice:0.##})"),
                customers.Select(c => c.Name));
        }

        /// <summary>A short, human description of an action, e.g. "Add product “Surf Excel 1 kg” for ₹500".</summary>
        public static string Describe(VoiceAction action)
        {
            switch (action.Type)
            {
                case VoiceAction.AddProduct:
                    return $"Add product “{ProductName(action)}” " + (action.Price > 0 ? $"for ₹{action.Price:0.##}" : "(no price heard)");
                case VoiceAction.UpdatePrice:
                    return $"Change the price of “{Clean(action.Name)}” to " + (action.Price > 0 ? $"₹{action.Price:0.##}" : "(no price heard)");
                case VoiceAction.AddCustomer:
                    var where = string.Join(", ", new[] { Clean(action.Address), Clean(action.City) }.Where(s => s.Length > 0));
                    return $"Add customer “{CustomerName(action)}”" + (where.Length > 0 ? $" ({where})" : "");
                case VoiceAction.CreateInvoice:
                    var items = (action.Items ?? new List<VoiceInvoiceItem>()).Select(i => $"{(i.Quantity > 0 ? i.Quantity : 1):0.##} × {Clean(i.Product)}");
                    return $"Invoice for “{Clean(action.Customer)}”: {string.Join(", ", items)}";
                default:
                    return $"Nothing to do ({Fallback(action.Reason, "not a command")})";
            }
        }

        /// <summary>
        /// Names the AI corrected compared to what Whisper heard, e.g. heard “Surfix Cell”, used “Surf Excel”.
        /// Only new names are checked; names of existing records are matches, not corrections.
        /// </summary>
        public static List<(string Heard, string Used)> SpellingFixes(string spokenText, VoiceAction action)
        {
            var fixes = new List<(string, string)>();
            var name = action.Type == VoiceAction.AddProduct ? Clean(action.Name)
                : action.Type == VoiceAction.AddCustomer ? CustomerName(action)
                : null;
            if (string.IsNullOrEmpty(name))
            {
                return fixes;
            }

            var spokenWords = Normalize(spokenText).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var nameWords = Normalize(name).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var spokenJoined = string.Join("", spokenWords);
            if (nameWords.Length == 0 || nameWords.All(w => spokenWords.Contains(w)) || spokenJoined.Contains(string.Join("", nameWords)))
            {
                return fixes;   // said as written (also "parle g" vs "Parle-G")
            }

            // Find the run of spoken words that looks most like the name.
            var target = string.Join(" ", nameWords);
            (string Text, double Score) best = (null, 0);
            for (var length = Math.Max(1, nameWords.Length - 1); length <= nameWords.Length + 1; length++)
            {
                for (var start = 0; start + length <= spokenWords.Length; start++)
                {
                    var candidate = string.Join(" ", spokenWords.Skip(start).Take(length));
                    var score = Similarity(candidate, target);
                    if (score > best.Score)
                    {
                        best = (candidate, score);
                    }
                }
            }
            if (best.Text != null && best.Score >= 0.4)
            {
                fixes.Add((best.Text, name));
            }
            return fixes;
        }

        /// <summary>Step 2: check each action against the database and save it.</summary>
        public async Task<List<VoiceActionResult>> RunAsync(IEnumerable<VoiceAction> actions, VoiceScreen screen)
        {
            var list = actions.ToList();
            var results = new List<VoiceActionResult>();

            var commands = list.Where(a => a.Type != VoiceAction.Unknown).ToList();
            if (commands.Count == 0)
            {
                var reason = list.Select(a => a.Reason).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
                results.Add(new VoiceActionResult()
                {
                    Kind = NotificationSeverity.Info,
                    Title = "No command found",
                    Message = $"That didn't sound like a command{(reason != null ? $" ({reason.Trim()})" : "")}, so I did nothing.",
                    Details = { $"Try: “{ExamplesFor(screen)[0]}”" }
                });
                return results;
            }

            foreach (var action in commands)
            {
                try
                {
                    var home = HomeScreen[action.Type];
                    if (home.Screen != screen)
                    {
                        results.Add(new VoiceActionResult()
                        {
                            Kind = NotificationSeverity.Info,
                            Title = $"That belongs on the {home.Screen} screen",
                            Message = $"You asked: {Describe(action)}. This is the {screen} screen, so I didn't do it here.",
                            Link = home.Page,
                            LinkText = $"Open {home.Screen}"
                        });
                        continue;
                    }

                    switch (action.Type)
                    {
                        case VoiceAction.AddProduct: results.Add(await AddProduct(action)); break;
                        case VoiceAction.UpdatePrice: results.Add(await UpdatePrice(action)); break;
                        case VoiceAction.AddCustomer: results.Add(await AddCustomer(action)); break;
                        case VoiceAction.CreateInvoice: results.Add(await CreateInvoice(action)); break;
                    }
                }
                catch (Exception ex)
                {
                    results.Add(Problem(NotificationSeverity.Error, "Could not save", $"{Describe(action)} failed: {ex.Message}"));
                }
            }
            return results;
        }

        private async Task<VoiceActionResult> AddProduct(VoiceAction action)
        {
            var name = ProductName(action);
            if (Clean(action.Name).Length == 0)
            {
                return Problem(NotificationSeverity.Warning, "Product name missing", "I didn't catch the product's name.", "Try: “Add Surf Excel 1 kg for 500 rupees”");
            }
            if (name.Length > 100)
            {
                return Problem(NotificationSeverity.Warning, "Name too long", $"“{name}” is longer than 100 characters.");
            }
            if (!(action.Price > 0))
            {
                return Problem(NotificationSeverity.Warning, "Price missing", $"I heard “{name}” but no price, so I didn't add it.", $"Try: “{name} for 50 rupees”");
            }

            var existing = (await simpleInvoice.GetProducts()).AsEnumerable().FirstOrDefault(p => Normalize(p.Name) == Normalize(name));
            if (existing != null)
            {
                var result = Problem(NotificationSeverity.Warning, "Already exists", $"“{existing.Name}” is already in your products at ₹{existing.UnitPrice:0.##}, so I didn't add it again.",
                    $"To change its price, say: “{existing.Name} is now {action.Price:0.##} rupees”");
                result.Link = $"edit-product/{existing.ProductId}";
                result.LinkText = "Open product";
                return result;
            }

            var product = await simpleInvoice.CreateProduct(new Product() { Name = name, UnitPrice = action.Price.Value });
            return Done("Product added", $"{product.Name} — ₹{product.UnitPrice:0.##}", $"edit-product/{product.ProductId}", "Open product");
        }

        private async Task<VoiceActionResult> UpdatePrice(VoiceAction action)
        {
            var spoken = Clean(action.Name);
            if (!(action.Price > 0))
            {
                return Problem(NotificationSeverity.Warning, "New price missing", $"I didn't hear the new price for “{spoken}”.", $"Try: “{spoken} is now 50 rupees”");
            }

            var products = (await simpleInvoice.GetProducts()).ToList();
            var (product, problem, note) = FindOne(products, p => p.Name, spoken, "product");
            if (product == null)
            {
                return Problem(NotificationSeverity.Warning, "Product not found", problem, "Check the name in the list below, or add it first: “Add … for … rupees”");
            }

            var oldPrice = product.UnitPrice;
            if (oldPrice == action.Price.Value)
            {
                return Problem(NotificationSeverity.Info, "No change", $"“{product.Name}” already costs ₹{oldPrice:0.##}.");
            }

            product.UnitPrice = action.Price.Value;
            await simpleInvoice.UpdateProduct(product.ProductId, product);
            var result = Done("Price updated", $"{product.Name}: ₹{oldPrice:0.##} → ₹{product.UnitPrice:0.##}", $"edit-product/{product.ProductId}", "Open product");
            if (note != null)
            {
                result.Details.Add(note);
            }
            return result;
        }

        private async Task<VoiceActionResult> AddCustomer(VoiceAction action)
        {
            var name = CustomerName(action);
            if (name.Length == 0)
            {
                return Problem(NotificationSeverity.Warning, "Customer name missing", "I didn't catch the customer's name.", "Try: “New customer Ali Traders, Mall Road, Lahore”");
            }

            var existing = (await simpleInvoice.GetCustomers()).AsEnumerable().FirstOrDefault(c => Normalize(c.Name) == Normalize(name));
            if (existing != null)
            {
                var result = Problem(NotificationSeverity.Warning, "Already exists", $"Customer “{existing.Name}” is already in your list, so I didn't add it again.");
                result.Link = $"edit-customer/{existing.CustomerId}";
                result.LinkText = "Open customer";
                return result;
            }

            var customer = await simpleInvoice.CreateCustomer(new Customer()
            {
                Name = name,
                Address = NullIfEmpty(Clean(action.Address)),
                City = NullIfEmpty(Clean(action.City))
            });
            var added = Done("Customer added", customer.Name, $"edit-customer/{customer.CustomerId}", "Open customer");
            if (customer.Address != null) added.Details.Add($"Address: {customer.Address}");
            if (customer.City != null) added.Details.Add($"City: {customer.City}");
            if (customer.Address == null && customer.City == null) added.Details.Add("No address or city heard. You can add them with “Open customer”.");
            return added;
        }

        private async Task<VoiceActionResult> CreateInvoice(VoiceAction action)
        {
            var customers = (await simpleInvoice.GetCustomers()).ToList();
            var (customer, customerProblem, customerNote) = FindOne(customers, c => c.Name, Clean(action.Customer), "customer");
            if (customer == null)
            {
                var result = Problem(NotificationSeverity.Warning, "Customer not found", $"Invoice not created. {customerProblem}", "Add the customer first on the Customers screen.");
                result.Link = "customers";
                result.LinkText = "Open Customers";
                return result;
            }

            var items = (action.Items ?? new List<VoiceInvoiceItem>()).Where(i => Clean(i.Product).Length > 0).ToList();
            if (items.Count == 0)
            {
                return Problem(NotificationSeverity.Warning, "No products heard", $"Invoice for {customer.Name} not created: I didn't hear which products to add.", $"Try: “Invoice for {customer.Name}, 2 Surf Excel 1 kg”");
            }

            // Match every item before saving anything, so a half-made invoice is never created.
            var products = (await simpleInvoice.GetProducts()).ToList();
            var notes = new List<string>();
            if (customerNote != null) notes.Add(customerNote);
            var lines = new List<(Product Product, double Qty)>();
            foreach (var item in items)
            {
                var (product, problem, note) = FindOne(products, p => p.Name, Clean(item.Product), "product");
                if (product == null)
                {
                    var result = Problem(NotificationSeverity.Warning, "Product not found", $"Invoice not created. {problem}", "Add the product first on the Products screen, then try again.");
                    result.Link = "products";
                    result.LinkText = "Open Products";
                    return result;
                }
                if (note != null) notes.Add(note);

                var qty = item.Quantity > 0 ? item.Quantity : 1;
                var index = lines.FindIndex(l => l.Product.ProductId == product.ProductId);
                if (index >= 0)
                {
                    lines[index] = (product, lines[index].Qty + qty);
                }
                else
                {
                    lines.Add((product, qty));
                }
            }

            // Same defaults and numbering as the Add Invoice page.
            var company = (await simpleInvoice.GetCompanies()).OrderBy(c => c.CompanyId).FirstOrDefault();
            var tax = (await simpleInvoice.GetTaxes()).OrderBy(t => t.TaxId).FirstOrDefault();
            var now = DateTime.Now;
            var invoice = await simpleInvoice.CreateInvoice(new Invoice()
            {
                InvoiceNumber = $"DRAFT-{now:yyyyMMddHHmmss}",
                InvoiceDate = now,
                InvoiceDueDate = now,
                CustomerId = customer.CustomerId,
                CompanyId = company?.CompanyId,
                TaxId = tax?.TaxId
            });

            var invoiceLines = new List<InvoiceLine>();
            foreach (var (product, qty) in lines)
            {
                var line = new InvoiceLine() { InvoiceId = invoice.InvoiceId, ProductId = product.ProductId, Qty = qty, UnitPrice = product.UnitPrice };
                line.RecalculateTotal();
                invoiceLines.Add(await simpleInvoice.CreateInvoiceLine(line));
            }

            invoice.InvoiceNumber = $"INV#{invoice.InvoiceId.ToString().PadLeft(4, '0')}";
            invoice.InvoiceLines = invoiceLines;
            invoice.Tax = tax;
            invoice.RecalculateTotal();
            await simpleInvoice.UpdateInvoice(invoice.InvoiceId, invoice);

            var done = Done($"Invoice {invoice.InvoiceNumber} created", $"For {customer.Name} — grand total ₹{invoice.GrandTotal:0.##}", $"edit-invoice/{invoice.InvoiceId}", $"Open {invoice.InvoiceNumber}");
            done.Details.AddRange(lines.Select(l => $"{l.Qty:0.##} × {l.Product.Name} @ ₹{l.Product.UnitPrice:0.##} = ₹{l.Qty * l.Product.UnitPrice:0.##}"));
            done.Details.Add($"Subtotal ₹{invoice.Total:0.##}" + (tax != null ? $" · {tax.Name} {tax.TaxTariffPercentage:0.##}% ₹{invoice.TaxAmount:0.##}" : ""));
            done.Details.AddRange(notes);
            return done;
        }

        /// <summary>
        /// Finds exactly one record by a spoken name: exact match first, then "contains", then closest spelling.
        /// Returns a problem message when nothing or more than one record matches, and a note when the match was not exact.
        /// </summary>
        private static (T Match, string Problem, string Note) FindOne<T>(List<T> items, Func<T, string> nameOf, string spoken, string kind) where T : class
        {
            var key = Normalize(spoken);
            if (key.Length == 0)
            {
                return (null, $"I didn't hear which {kind}.", null);
            }

            var exact = items.Where(i => Normalize(nameOf(i)) == key).ToList();
            if (exact.Count == 1)
            {
                return (exact[0], null, null);
            }

            var containing = items.Where(i => Normalize(nameOf(i)).Contains(key) || key.Contains(Normalize(nameOf(i)))).ToList();
            if (containing.Count == 1)
            {
                return (containing[0], null, $"“{spoken}” matched to “{nameOf(containing[0])}”");
            }
            if (containing.Count > 1)
            {
                return (null, $"More than one {kind} matches “{spoken}”: {string.Join(", ", containing.Take(5).Select(nameOf))}. Please say the full name.", null);
            }

            var closest = items
                .Select(i => (Item: i, Score: Similarity(Normalize(nameOf(i)), key)))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (closest.Item != null && closest.Score >= 0.75)
            {
                return (closest.Item, null, $"“{spoken}” matched to “{nameOf(closest.Item)}” (closest spelling)");
            }
            return (null, $"I couldn't find a {kind} called “{spoken}”.", null);
        }

        private static string ProductName(VoiceAction action)
        {
            var name = Clean(action.Name);
            var size = Clean(action.Size);
            return size.Length > 0 && !name.EndsWith(size, StringComparison.OrdinalIgnoreCase) ? $"{name} {size}" : name;
        }

        private static string CustomerName(VoiceAction action) => Clean(action.Name).Length > 0 ? Clean(action.Name) : Clean(action.Customer);

        private static double Similarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0)
            {
                return 0;
            }
            var d = new int[a.Length + 1, b.Length + 1];
            for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (var j = 0; j <= b.Length; j++) d[0, j] = j;
            for (var i = 1; i <= a.Length; i++)
            {
                for (var j = 1; j <= b.Length; j++)
                {
                    var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                }
            }
            return 1.0 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
        }

        private static string Normalize(string s) => Regex.Replace((s ?? string.Empty).ToLowerInvariant(), @"[^a-z0-9.]+", " ").Trim();

        private static string Clean(string s) => Regex.Replace(s ?? string.Empty, @"\s+", " ").Trim();

        private static string NullIfEmpty(string s) => s.Length == 0 ? null : s;

        private static string Fallback(string s, string fallback) => string.IsNullOrWhiteSpace(s) ? fallback : s.Trim();

        private static VoiceActionResult Done(string title, string message, string link, string linkText) =>
            new VoiceActionResult() { Kind = NotificationSeverity.Success, Title = title, Message = message, Link = link, LinkText = linkText };

        private static VoiceActionResult Problem(NotificationSeverity kind, string title, string message, string tip = null)
        {
            var result = new VoiceActionResult() { Kind = kind, Title = title, Message = message };
            if (tip != null)
            {
                result.Details.Add(tip);
            }
            return result;
        }
    }
}
