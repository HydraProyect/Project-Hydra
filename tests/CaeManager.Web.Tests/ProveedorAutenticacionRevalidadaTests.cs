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

/// <summary>
/// Fallo cerrado de la revalidación del circuito: un error al consultar la base
/// (también una cancelación que no es la del ciclo, como un tiempo de espera del
/// proveedor) invalida la sesión tras repetir la consulta una vez; la cancelación
/// del propio ciclo no invalida nada y se relanza con su token. El recorrido con
/// base real está en <c>SesionDeCuentaDesactivadaTests</c> (integración).
/// </summary>
public class ProveedorAutenticacionRevalidadaTests
{
    public enum Caso { ErrorDeBase, CancelacionAjena, CancelacionDelCiclo, ErrorDuranteLaCancelacionDelCiclo }

    [Theory]
    [InlineData(Caso.ErrorDeBase)]
    [InlineData(Caso.CancelacionAjena)]
    [InlineData(Caso.CancelacionDelCiclo)]
    [InlineData(Caso.ErrorDuranteLaCancelacionDelCiclo)]
    public async Task Un_error_invalida_la_sesion_salvo_cancelacion_del_ciclo(Caso caso)
    {
        using var ciclo = new CancellationTokenSource();
        using var otro = new CancellationTokenSource();
        var cicloCancelado = caso is Caso.CancelacionDelCiclo or Caso.ErrorDuranteLaCancelacionDelCiclo;
        if (cicloCancelado) ciclo.Cancel();
        otro.Cancel();
        Exception error = caso switch
        {
            Caso.CancelacionAjena => new OperationCanceledException(otro.Token),
            Caso.CancelacionDelCiclo => new OperationCanceledException(ciclo.Token),
            Caso.ErrorDuranteLaCancelacionDelCiclo => new ObjectDisposedException("ambito"),
            _ => new ErrorBaseDeDatos(),
        };
        var ambitos = new AmbitosConError(error);
        var proveedor = new ProveedorAutenticacionRevalidada(NullLoggerFactory.Instance, ambitos,
            Options.Create(new SecurityStampValidatorOptions()));
        try
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "prueba"),
                new Claim(CaeManager.Infrastructure.Identity.SignInManagerCuentaDesactivada.ClaimSid, Guid.NewGuid().ToString("N")),
                new Claim(CaducidadSesion.MarcaInicio, DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture))], "sesion"));
            var metodo = typeof(ProveedorAutenticacionRevalidada).GetMethod("ValidateAuthenticationStateAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var tarea = (Task<bool>)metodo.Invoke(proveedor, [new AuthenticationState(principal), ciclo.Token])!;
            if (cicloCancelado)
            {
                var excepcion = await tarea.Invoking(t => t).Should().ThrowAsync<TaskCanceledException>();
                excepcion.Which.CancellationToken.Should().Be(ciclo.Token);
                proveedor.SesionInvalidada.Should().BeFalse();
                ambitos.Llamadas.Should().Be(1, "con el ciclo cancelado no se repite la consulta");
            }
            else
            {
                (await tarea).Should().BeFalse();
                proveedor.SesionInvalidada.Should().BeTrue();
                ambitos.Llamadas.Should().Be(2, "la consulta se repite una vez antes de fallar cerrado");
            }
        }
        finally
        {
            ((IDisposable)proveedor).Dispose();
        }
    }

    [Fact]
    public async Task Un_circuito_anonimo_no_consulta_la_base()
    {
        var ambitos = new AmbitosConError(new ErrorBaseDeDatos());
        var proveedor = new ProveedorAutenticacionRevalidada(NullLoggerFactory.Instance, ambitos,
            Options.Create(new SecurityStampValidatorOptions()));
        try
        {
            var metodo = typeof(ProveedorAutenticacionRevalidada).GetMethod("ValidateAuthenticationStateAsync",
                BindingFlags.NonPublic | BindingFlags.Instance)!;
            var tarea = (Task<bool>)metodo.Invoke(proveedor,
                [new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())), CancellationToken.None])!;
            (await tarea).Should().BeTrue();
            proveedor.SesionInvalidada.Should().BeFalse();
            ambitos.Llamadas.Should().Be(0);
        }
        finally
        {
            ((IDisposable)proveedor).Dispose();
        }
    }

    private sealed class ErrorBaseDeDatos : DbException;

    private sealed class AmbitosConError(Exception error) : IServiceScopeFactory
    {
        public int Llamadas { get; private set; }

        public IServiceScope CreateScope()
        {
            Llamadas++;
            throw error;
        }
    }
}
