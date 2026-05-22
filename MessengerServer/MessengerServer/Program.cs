using Microsoft.EntityFrameworkCore;
using MessengerServer.Data;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<AppDbContext>(opts =>
    opts.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));
builder.Services.AddControllers();
var app = builder.Build();
app.MapControllers();
app.Run();