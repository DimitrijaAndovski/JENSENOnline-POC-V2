using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using JENSENOnline.POC.Data;
using JENSENOnline.POC.Dtos;
using JENSENOnline.POC.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseSqlite(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

var jwtKey = builder.Configuration["Jwt:Key"]!;
var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (context.Request.Cookies.TryGetValue("auth_token", out var token))
                    context.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

var app = builder.Build();

// Central felhantering. Användaren ska inte få tekniska felmeddelanden.
app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = 500;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { message = "Ett oväntat fel uppstod." });
    });
});

// Extra VG-skydd: enkla security headers.
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'";
    await next();
});

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
    db.Database.EnsureCreated();

    if (!db.Products.Any())
    {
        db.Products.AddRange(
            new Product { Name = "Tangentbord", Price = 699, Stock = 20 },
            new Product { Name = "Webbkamera 1080p", Price = 399, Stock = 15 },
            new Product { Name = "USB-C Hub", Price = 499, Stock = 30 });
    }

    if (!db.Users.Any(x => x.Email == "admin@jensen.local"))
    {
        var admin = new AppUser { Email = "admin@jensen.local", Role = "Admin" };
        admin.PasswordHash = hasher.HashPassword(admin, "Admin123!");
        db.Users.Add(admin);
    }
    db.SaveChanges();
}

bool ValidEmail(string email)
{
    return !string.IsNullOrWhiteSpace(email) &&
           email.Length <= 100 &&
           email.Contains("@");
}

bool StrongPassword(string password)
{
    return password.Length >= 8 &&
           password.Any(char.IsUpper) &&
           password.Any(char.IsLower) &&
           password.Any(char.IsDigit);
}

string CreateToken(AppUser user)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(ClaimTypes.Email, user.Email),
        new Claim(ClaimTypes.Role, user.Role)
    };
    var token = new JwtSecurityToken(
        issuer: builder.Configuration["Jwt:Issuer"],
        audience: builder.Configuration["Jwt:Audience"],
        claims: claims,
        expires: DateTime.UtcNow.AddMinutes(30),
        signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256));
    return new JwtSecurityTokenHandler().WriteToken(token);
}

int CurrentUserId(ClaimsPrincipal user) =>
    int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

app.MapPost("/api/auth/register", async (RegisterRequest req, AppDbContext db, IPasswordHasher<AppUser> hasher) =>
{
    var email = req.Email.Trim().ToLowerInvariant();
    if (!ValidEmail(email) || !StrongPassword(req.Password))
        return Results.BadRequest(new { message = "Ogiltig e-post eller lösenord. Lösenord: minst 8 tecken, stor bokstav, liten bokstav och siffra." });
    if (await db.Users.AnyAsync(x => x.Email == email))
        return Results.BadRequest(new { message = "Kontot kan inte skapas." });

    var user = new AppUser { Email = email, Role = "Customer" }; // Rollen bestäms av servern.
    user.PasswordHash = hasher.HashPassword(user, req.Password);
    db.Users.Add(user);
    await db.SaveChangesAsync();
    return Results.Ok(new { message = "Konto skapat." });
});

app.MapPost("/api/auth/login", async (LoginRequest req, AppDbContext db, IPasswordHasher<AppUser> hasher, HttpContext http, ILogger<Program> log) =>
{
    var email = req.Email.Trim().ToLowerInvariant();
    var user = await db.Users.SingleOrDefaultAsync(x => x.Email == email);
    if (user is null)
    {
        log.LogWarning("Failed login for unknown account");
        return Results.Unauthorized();
    }

    if (user.LockoutEndUtc is not null && user.LockoutEndUtc > DateTime.UtcNow)
        return Results.Json(new { message = "Kontot är tillfälligt låst." }, statusCode: 423);

    var verified = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
    if (verified == PasswordVerificationResult.Failed)
    {
        user.FailedLoginAttempts++;
        if (user.FailedLoginAttempts >= 5)
        {
            user.LockoutEndUtc = DateTime.UtcNow.AddMinutes(5);
            user.FailedLoginAttempts = 0;
        }
        await db.SaveChangesAsync();
        log.LogWarning("Failed login for user id {UserId}", user.Id);
        return Results.Unauthorized();
    }

    user.FailedLoginAttempts = 0;
    user.LockoutEndUtc = null;
    await db.SaveChangesAsync();

    var token = CreateToken(user);
    http.Response.Cookies.Append("auth_token", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Strict,
        MaxAge = TimeSpan.FromMinutes(30)
    });
    return Results.Ok(new { user.Email, user.Role });
});

app.MapPost("/api/auth/logout", (HttpContext http) =>
{
    http.Response.Cookies.Delete("auth_token");
    return Results.Ok(new { message = "Utloggad." });
});

app.MapGet("/api/auth/me", (ClaimsPrincipal user) => Results.Ok(new
{
    email = user.FindFirstValue(ClaimTypes.Email),
    role = user.FindFirstValue(ClaimTypes.Role)
})).RequireAuthorization();

app.MapGet("/api/products", async (string? search, AppDbContext db) =>
{
    var q = db.Products.AsNoTracking().AsQueryable();
    if (!string.IsNullOrWhiteSpace(search))
    {
        var safeSearch = search.Trim();
        if (safeSearch.Length > 50) return Results.BadRequest(new { message = "Sökningen är för lång." });
        q = q.Where(x => x.Name.Contains(safeSearch));
    }
    return Results.Ok(await q.OrderBy(x => x.Name).ToListAsync());
});

app.MapPost("/api/admin/products", async (ProductRequest req, AppDbContext db, ClaimsPrincipal user, ILogger<Program> log) =>
{
    var name = req.Name.Trim();
    if (name.Length < 2 || name.Length > 80 || req.Price <= 0 || req.Price > 100000 || req.Stock < 0 || req.Stock > 100000)
        return Results.BadRequest(new { message = "Ogiltiga produktdata." });
    var product = new Product { Name = name, Price = req.Price, Stock = req.Stock };
    db.Products.Add(product);
    await db.SaveChangesAsync();
    log.LogInformation("AUDIT admin {Admin} created product {ProductId}", user.FindFirstValue(ClaimTypes.NameIdentifier), product.Id);
    return Results.Created($"/api/products/{product.Id}", product);
}).RequireAuthorization(p => p.RequireRole("Admin"));

app.MapPut("/api/admin/products/{id:int}", async (int id, ProductRequest req, AppDbContext db, ClaimsPrincipal user, ILogger<Program> log) =>
{
    var product = await db.Products.FindAsync(id);
    if (product is null) return Results.NotFound();
    var name = req.Name.Trim();
    if (name.Length < 2 || name.Length > 80 || req.Price <= 0 || req.Price > 100000 || req.Stock < 0 || req.Stock > 100000)
        return Results.BadRequest(new { message = "Ogiltiga produktdata." });
    product.Name = name; product.Price = req.Price; product.Stock = req.Stock;
    await db.SaveChangesAsync();
    log.LogInformation("AUDIT admin {Admin} updated product {ProductId}", user.FindFirstValue(ClaimTypes.NameIdentifier), product.Id);
    return Results.Ok(product);
}).RequireAuthorization(p => p.RequireRole("Admin"));

app.MapPost("/api/orders", async (CreateOrderRequest req, AppDbContext db, ClaimsPrincipal user) =>
{
    if (req.Items is null || req.Items.Count == 0 || req.Items.Count > 20)
        return Results.BadRequest(new { message = "Ordern måste innehålla 1-20 orderrader." });
    if (req.Items.Any(x => x.Quantity < 1 || x.Quantity > 20))
        return Results.BadRequest(new { message = "Ogiltigt antal." });

    var ids = req.Items.Select(x => x.ProductId).Distinct().ToList();
    var products = await db.Products.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
    if (products.Count != ids.Count) return Results.BadRequest(new { message = "En produkt saknas." });

    var order = new Order { UserId = CurrentUserId(user) };
    foreach (var line in req.Items)
    {
        var p = products[line.ProductId];
        order.Items.Add(new OrderItem { ProductId = p.Id, ProductName = p.Name, UnitPrice = p.Price, Quantity = line.Quantity });
    }
    order.Total = order.Items.Sum(x => x.UnitPrice * x.Quantity); // Priset hämtas från databasen, inte från klienten.
    db.Orders.Add(order);
    await db.SaveChangesAsync();
    return Results.Ok(new { order.Id, order.Total, order.CreatedUtc });
}).RequireAuthorization();

app.MapGet("/api/orders", async (AppDbContext db, ClaimsPrincipal user) =>
{
    var userId = CurrentUserId(user);
    var orders = await db.Orders.AsNoTracking().Include(x => x.Items)
        .Where(x => x.UserId == userId).OrderByDescending(x => x.CreatedUtc).ToListAsync();
    return Results.Ok(orders);
}).RequireAuthorization();

app.MapGet("/api/orders/{id:int}", async (int id, AppDbContext db, ClaimsPrincipal user) =>
{
    var userId = CurrentUserId(user);
    var order = await db.Orders.AsNoTracking().Include(x => x.Items)
        .SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId); // Kunden får bara läsa sin egen order.
    return order is null ? Results.NotFound() : Results.Ok(order);
}).RequireAuthorization();

app.MapGet("/api/admin/orders", async (AppDbContext db) =>
    Results.Ok(await db.Orders.AsNoTracking().Include(x => x.Items).OrderByDescending(x => x.CreatedUtc).ToListAsync()))
    .RequireAuthorization(p => p.RequireRole("Admin"));

app.MapFallbackToFile("index.html");
app.Run();
