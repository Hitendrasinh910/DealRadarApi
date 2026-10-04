using DealRadar.Api.Data;
using DealRadar.Api.Services;
using DealRadar.Api.Workers;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 1. Register SQL Server DbContext
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Register Scraper (Singleton) and Telegram Service
builder.Services.AddSingleton<SmartPriceScraperService>();
builder.Services.AddHttpClient<TelegramNotificationService>();

// 3. Register Automatic 10-Minute Background Worker
builder.Services.AddHostedService<PriceMonitorWorker>();

// 4. Enable CORS for Angular (http://localhost:4200)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
        policy.WithOrigins("http://localhost:4200")
              .AllowAnyHeader()
              .AllowAnyMethod());
});

builder.Services.AddMemoryCache();

var app = builder.Build();

//if (app.Environment.IsDevelopment())
//{
    app.UseSwagger();
    app.UseSwaggerUI();
//}

app.UseHttpsRedirection();
app.UseCors("AllowAngular");
app.UseAuthorization();
app.MapControllers();

app.Run();