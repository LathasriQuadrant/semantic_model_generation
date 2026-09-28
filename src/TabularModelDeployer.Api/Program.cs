using TabularModelDeployer.Api.Services;
using Microsoft.AspNetCore.Diagnostics;

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

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async ctx =>
    {
        var ex = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
        ctx.Response.StatusCode = 500;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsJsonAsync(new
        {
            success = false,
            error = ex?.Message,
            type = ex?.GetType().Name,
            inner = ex?.InnerException?.Message
        });
    });
});

app.MapControllers();

app.Run();


