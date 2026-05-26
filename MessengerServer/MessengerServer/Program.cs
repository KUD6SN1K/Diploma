using MessengerServer.Data;
using MessengerServer.Services;
using Microsoft.EntityFrameworkCore;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddControllers();
builder.Services.AddSingleton<ConnectionManager>();
var app = builder.Build();
app.UseWebSockets();
app.MapControllers();
app.Run();