using System.Data.Common;
using System.Reflection;
using System.Security.Claims;
using CaeManager.Web.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

public class ProveedorAutenticacionRevalidadaTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Un_error_invalida_la_sesion_salvo_cancelacion_del_ciclo(bool cancelado)
    {
        using var ciclo = new CancellationTokenSource();
        if (cancelado) ciclo.Cancel();
        var proveedor = new ProveedorAutenticacionRevalidada(NullLoggerFactory.Instance,
            new AmbitosConError(cancelado ? new OperationCanceledException(ciclo.Token) : new ErrorBaseDeDatos()),
            Options.Create(new SecurityStampValidatorOptions()));
        try
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "prueba")], "sesion"));
            var metodo = typeof(ProveedorAutenticacionRevalidada).GetMethod("ValidateAuthenticationStateAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var tarea = (Task<bool>)metodo.Invoke(proveedor, [new AuthenticationState(principal), ciclo.Token])!;
            if (cancelado)
            {
                var error = await tarea.Invoking(t => t).Should().ThrowAsync<TaskCanceledException>();
                error.Which.CancellationToken.Should().Be(ciclo.Token);
                proveedor.SesionInvalidada.Should().BeFalse();
            }
            else
            {
                (await tarea).Should().BeFalse();
                proveedor.SesionInvalidada.Should().BeTrue();
            }
        }
        finally
        {
            ((IDisposable)proveedor).Dispose();
        }
    }

    private sealed class ErrorBaseDeDatos : DbException;
    private sealed class AmbitosConError(Exception error) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw error;
    }
}
