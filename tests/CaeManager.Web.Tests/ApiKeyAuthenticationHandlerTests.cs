using CaeManager.Application.Common;
using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autenticacion;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;

namespace CaeManager.Web.Tests;

public class ApiKeyAuthenticationHandlerTests
{
    // Autenticar exige que siga abierta la ventana de Soporte en que se emitió la clave
    // (CriterioSoporteClaveApi.PuedeAutenticar): con la ventana cerrada, retirada, vencida o
    // reabierta, la clave ya no vale. El Tenant propietario se comprueba en ClaveApiRepository.
    [Theory]
    [InlineData("legitima", true)]
    [InlineData("caducada", false)]
    [InlineData("revocada", false)]
    [InlineData("soporte con ventana cerrada", false)]
    [InlineData("soporte retirado", false)]
    [InlineData("soporte vencido", false)]
    [InlineData("soporte reabierto tras emitir", false)]
    [InlineData("soporte de otro origen", false)]
    [InlineData("delegacion comercial del mismo origen", false)]
    [InlineData("soporte hacia otro Tenant", false)]
    [InlineData("sin delegacion", false)]
    [InlineData("cuenta emisora eliminada", false)]
    [InlineData("cuenta emisora desactivada", false)]
    public async Task Autentica_una_clave_vigente_si_sigue_abierta_la_ventana_de_soporte_en_que_se_emitio(string caso, bool acepta)
    {
        const string secreto = "hydra_clave-ficticia-de-prueba";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secreto))).ToLowerInvariant();
        var tenant = Guid.NewGuid();
        var origen = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        var clave = new ClaveApi("ERP Alfa", "hydra_prueba", hash, Guid.NewGuid(), ahora.AddDays(30), "Prueba");
        Asignar(clave, nameof(ClaveApi.TenantId), tenant);
        if (caso == "caducada")
            Asignar(clave, nameof(ClaveApi.CaducaEnUtc), ahora.AddDays(-1));
        if (caso == "revocada") clave.Revocar();
        var repositorio = new Repositorio(clave, tenant,
            caso == "cuenta emisora eliminada" ? null : origen, Delegaciones(caso, origen, tenant, ahora),
            cuentaRespalda: caso != "cuenta emisora desactivada");
        var guardado = new Guardado();
        using var servicios = new ServiceCollection().AddLogging().BuildServiceProvider();
        using var logger = LoggerFactory.Create(_ => { });
        var contexto = new DefaultHttpContext { RequestServices = servicios };
        contexto.Request.Headers.Authorization = $"ApiKey {secreto}";
        var handler = new ApiKeyAuthenticationHandler(new Opciones(), logger, UrlEncoder.Default, repositorio, guardado);
        await handler.InitializeAsync(new AuthenticationScheme("ApiKey", null, typeof(ApiKeyAuthenticationHandler)), contexto);

        var resultado = await handler.AuthenticateAsync();

        resultado.Succeeded.Should().Be(acepta);
        guardado.Veces.Should().Be(acepta ? 1 : 0);
        clave.UltimoUsoUtc.HasValue.Should().Be(acepta);
        if (acepta)
        {
            resultado.Principal!.FindFirstValue("tenant_id").Should().Be(tenant.ToString());
            resultado.Principal!.FindFirstValue(ClaimTypes.Role).Should().Be("Consulta");
            repositorio.RespaldoComprobado.Should().Be((tenant, clave.CreadaPorUsuarioId, clave.CreadoEnUtc),
                "el respaldo se mira en el Tenant propietario, en la cuenta emisora y contra la fecha de emisión");
        }
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull();
    }

    private static List<DelegacionTenant> Delegaciones(string caso, Guid origen, Guid tenant, DateTime ahora)
    {
        switch (caso)
        {
            case "sin delegacion":
                return [];
            case "delegacion comercial del mismo origen":
                return [new DelegacionTenant(origen, tenant)];
            case "soporte de otro origen":
                return [Abierta(DelegacionTenant.ParaSoporte(Guid.NewGuid(), tenant), ahora)];
            case "soporte hacia otro Tenant":
                return [Abierta(DelegacionTenant.ParaSoporte(origen, Guid.NewGuid()), ahora)];
            case "soporte con ventana cerrada":
                return [DelegacionTenant.ParaSoporte(origen, tenant)];
            case "soporte retirado":
                var retirada = Abierta(DelegacionTenant.ParaSoporte(origen, tenant), ahora);
                retirada.Desactivar();
                return [retirada];
            case "soporte reabierto tras emitir":
                var reabierta = DelegacionTenant.ParaSoporte(origen, tenant);
                reabierta.ActivarParaSoporte("Prueba", ahora.AddHours(1), ahora.AddMinutes(1));
                return [reabierta];
            case "soporte vencido":
                var vencida = DelegacionTenant.ParaSoporte(origen, tenant);
                vencida.ActivarParaSoporte("Prueba", ahora.AddMinutes(-1), ahora.AddHours(-2));
                vencida.EstaVigente(ahora).Should().BeFalse();
                return [vencida];
            default:
                return [Abierta(DelegacionTenant.ParaSoporte(origen, tenant), ahora)];
        }
    }

    private static DelegacionTenant Abierta(DelegacionTenant delegacion, DateTime ahora)
    {
        delegacion.ActivarParaSoporte("Prueba", ahora.AddHours(1), ahora.AddMinutes(-1));
        return delegacion;
    }

    // El setter privado de una propiedad heredada solo se ve desde el tipo que la declara.
    private static void Asignar(ClaveApi clave, string propiedad, object valor)
    {
        var declarada = typeof(ClaveApi).GetProperty(propiedad)!.DeclaringType!.GetProperty(propiedad)!;
        declarada.SetValue(clave, valor);
    }

    private sealed class Opciones : IOptionsMonitor<ApiKeyAuthenticationSchemeOptions>
    {
        public ApiKeyAuthenticationSchemeOptions CurrentValue { get; } = new();
        public ApiKeyAuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ApiKeyAuthenticationSchemeOptions, string?> listener) => null;
    }

    private sealed class Guardado : IUnitOfWork
    {
        public int Veces { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) { Veces++; return Task.FromResult(1); }
    }

    // Doble del repositorio que aplica el mismo criterio que ClaveApiRepository sobre una lista en
    // memoria: el origen de la cuenta emisora (null si se eliminó) y las delegaciones del caso.
    private sealed class Repositorio(ClaveApi clave, Guid tenant, Guid? origenEmisor, List<DelegacionTenant> delegaciones, bool cuentaRespalda) : IClaveApiRepository
    {
        public (Guid Tenant, Guid Cuenta, DateTime EmitidaEnUtc)? RespaldoComprobado { get; private set; }
        public Task<Guid?> ObtenerTenantPorHashAsync(string hashClave, CancellationToken cancellationToken = default)
        {
            AmbitoTenantExplicito.TenantIdActual.Should().BeNull();
            return Task.FromResult<Guid?>(hashClave == clave.HashClave ? tenant : null);
        }
        public Task<ClaveApi?> ObtenerPorHashAsync(string hashClave, CancellationToken cancellationToken = default)
        {
            AmbitoTenantExplicito.TenantIdActual.Should().Be(tenant);
            return Task.FromResult<ClaveApi?>(hashClave == clave.HashClave ? clave : null);
        }
        public Task<bool> ClaveSigueRespaldadaAsync(Guid tenantPropietarioId, Guid creadaPorUsuarioId, DateTime emitidaEnUtc, CancellationToken cancellationToken = default)
        {
            AmbitoTenantExplicito.TenantIdActual.Should().Be(tenant);
            RespaldoComprobado = (tenantPropietarioId, creadaPorUsuarioId, emitidaEnUtc);
            return Task.FromResult(cuentaRespalda && creadaPorUsuarioId == clave.CreadaPorUsuarioId && origenEmisor is { } o
                && delegaciones.AsQueryable().Any(CriterioSoporteClaveApi.PuedeAutenticar(o, tenantPropietarioId, emitidaEnUtc, DateTime.UtcNow)));
        }
        public Task<ClaveApi?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Agregar(ClaveApi clave) => throw new NotSupportedException();
    }
}
