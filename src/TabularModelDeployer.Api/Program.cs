using TabularModelDeployer.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// ✅ ADD CORS SERVICE (NEW)
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .WithOrigins("https://reportmigration-frontend-g9ceape5ddgxa5gq.eastus-01.azurewebsites.net")
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddHttpClient(); //new
builder.Services.AddScoped<PowerBiRestCredentialService>(); //new

builder.Services.AddScoped<TabularDeploymentService>();

var app = builder.Build();

// ✅ ENABLE CORS (NEW)
app.UseCors("AllowFrontend");

app.UseSwagger();
app.UseSwaggerUI();

app.UseAuthorization();
app.MapControllers();

app.Run();
