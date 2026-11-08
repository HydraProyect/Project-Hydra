using CaeManager.Application.Common;
using CaeManager.Application.Gestiones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.Gestiones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.IntegrationTests.Gestiones;

/// <summary>
/// La conciliación de Gestiones automáticas con el cableado de producción (<see cref="ArnesDeArranqueRuntime"/>): conexión
/// como <c>cae_app_runtime</c>, los interceptores de sellado y de sesión RLS, y el disparador real recorriendo los Tenants
/// con <see cref="AmbitoTenantExplicito"/>. Lo que mide y Application no puede: que PostgreSQL traduce sus consultas bajo
/// RLS, que cada Gestión queda sellada con el Tenant propietario de su Documento, que la ventana se aplica en SQL, y que
/// repetir la pasada no duplica nada.
/// </summary>
public class ConciliacionGestionesAutomaticasBajoRuntimeTests
{
    [Fact]
    public async Task Concilia_cada_Tenant_en_su_ambito_bajo_RLS_respeta_la_ventana_y_repetirla_no_duplica()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            serviciosAdicionales: s => s.AddScoped<IConciliacionGestionesAutomaticasService>(sp =>
            {
                var contexto = sp.GetRequiredService<CaeManagerDbContext>();
                return new ConciliacionGestionesAutomaticasService(
                    contexto, contexto, AltaAcreditacionesDePrueba.GestionAutomatica(contexto), contexto);
            }));

        Guid tenantPropio, tenantOtro, proveedorId;
        await using (var propietario = CrearContextoPropietario(arnes.CadenaPropietario))
        {
            var propio = new Tenant("Tenant propietario de la conciliación");
            var otro = new Tenant("Otro Tenant propietario");
            var proveedor = new ProveedorPlataformaCae($"prueba-{Guid.NewGuid():N}", "Plataforma de prueba");
            propietario.Tenants.AddRange(propio, otro);
            propietario.ProveedoresPlataformaCae.Add(proveedor);
            await propietario.SaveChangesAsync();
            (tenantPropio, tenantOtro, proveedorId) = (propio.Id, otro.Id, proveedor.Id);
        }

        var propioSinGestion = await SembrarAsync(arnes, tenantPropio, proveedorId, "Montajes Springfield");
        var otroSinGestion = await SembrarAsync(arnes, tenantOtro, proveedorId, "Montajes Shelbyville");
        var historico = await ActoSinGestionAsync(arnes, tenantPropio, propioSinGestion);
        await EnvejecerAsync(arnes, historico, TimeSpan.FromDays(3));
        var renovacion = await RenovacionSinGestionAsync(arnes, tenantPropio, propioSinGestion);

        await using (var scope = arnes.Servicios.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>().Database
                    .SqlQueryRaw<string>("SELECT current_user::text AS \"Value\"").SingleAsync())
                .Should().Be("cae_app_runtime", "control del instrumento: la conciliación corre con la identidad de tráfico");

        // Sin Tenant fijado: el disparador abre el ámbito de cada uno, como en producción.
        AmbitoTenantExplicito.TenantIdActual.Should().BeNull();
        var disparador = new ConciliacionGestionesAutomaticasHostedService(
            arnes.Servicios.GetRequiredService<IServiceScopeFactory>(), new LiderSiempre(),
            Options.Create(new ConciliacionGestionesAutomaticasOptions()), TimeProvider.System,
            NullLogger<ConciliacionGestionesAutomaticasHostedService>.Instance);
        var desde = DateTime.UtcNow.AddHours(-24);

        var primera = await disparador.ProcesarAsync(desde, CancellationToken.None);
        var segunda = await disparador.ProcesarAsync(desde, CancellationToken.None);

        primera.Completa.Should().BeTrue();
        primera.Creadas.Should().Be(3, "el acto de cada Tenant y la renovación; el histórico queda fuera de la ventana");
        segunda.Completa.Should().BeTrue();
        segunda.Creadas.Should().Be(0, "repetirla no duplica");

        await using var comprobacion = CrearContextoPropietario(arnes.CadenaPropietario);
        var gestiones = await comprobacion.GestionesAutomaticas.IgnoreQueryFilters().Include(g => g.Pasos).AsNoTracking().ToListAsync();
        gestiones.Should().OnlyHaveUniqueItems(g => g.DocumentoId);

        var delPropio = gestiones.Single(g => g.DocumentoId == propioSinGestion.Documento);
        delPropio.TenantId.Should().Be(tenantPropio);
        delPropio.Origen.Should().Be(OrigenGestionAutomatica.AltaDocumento);
        delPropio.Pasos.Should().ContainSingle().Which.AcreditacionId.Should().Be(propioSinGestion.Acreditacion);
        delPropio.Pasos.Single().TenantId.Should().Be(tenantPropio);

        var delOtro = gestiones.Single(g => g.DocumentoId == otroSinGestion.Documento);
        delOtro.TenantId.Should().Be(tenantOtro, "la pasada de cada Tenant sella con su Tenant propietario, no con el anterior");
        delOtro.Pasos.Should().ContainSingle().Which.AcreditacionId.Should().Be(otroSinGestion.Acreditacion);

        gestiones.Should().NotContain(g => g.DocumentoId == historico.Documento, "creado antes de la ventana: histórico");

        var renovada = gestiones.Single(g => g.DocumentoId == renovacion.Nuevo.Documento);
        renovada.Origen.Should().Be(OrigenGestionAutomatica.RenovacionDocumento);
        renovada.Pasos.Should().ContainSingle().Which.AcreditacionId.Should().Be(renovacion.Nuevo.Acreditacion);
        var anterior = gestiones.Single(g => g.Id == renovacion.GestionAnterior);
        anterior.EstaSustituida.Should().BeFalse("la sustitución la deriva la lectura; la conciliación no la marca");
    }

    private sealed record Sembrado(Guid Documento, Guid Acreditacion, Guid Canal, Guid Trabajador, Guid Tipo);

    private readonly DateOnly Hoy = DiaDeNegocio.Hoy();

    /// <summary>
    /// Lo que deja un alta con archivo atendida por la ranura anterior: el Documento y su acreditación de plataforma en
    /// el mismo guardado, sin Gestión.
    /// </summary>
    private async Task<Sembrado> SembrarAsync(ArnesDeArranqueRuntime arnes, Guid tenantId, Guid proveedorId, string razonSocial)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        await using var propietario = CrearContextoPropietario(arnes.CadenaPropietario);
        var empresa = new Empresa(razonSocial, "B87654323");
        var cliente = Empresa.CrearComoCliente($"Cliente de {razonSocial}", "B12345674", false, null, null);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Laura", "Gil", "77189989B");
        var tipo = new TipoDocumento("Reconocimiento médico", 12, aplicaVencimientoAutomatico: true, orden: 1,
            AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        var centro = new Centro(cliente.Id, empresa.Id, "Planta Norte");
        propietario.Empresas.AddRange(empresa, cliente);
        propietario.Trabajadores.Add(trabajador);
        propietario.TiposDocumento.Add(tipo);
        propietario.Centros.Add(centro);
        await propietario.SaveChangesAsync();

        var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Gestión general", proveedorId, null, null, null);
        propietario.CanalesGestionDocumental.Add(canal);
        await propietario.SaveChangesAsync();

        return await ActoSinGestionAsync(arnes, tenantId, new Sembrado(Guid.Empty, Guid.Empty, canal.Id, trabajador.Id, tipo.Id));
    }

    private async Task<Sembrado> ActoSinGestionAsync(ArnesDeArranqueRuntime arnes, Guid tenantId, Sembrado semilla)
    {
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        await using var propietario = CrearContextoPropietario(arnes.CadenaPropietario);
        var documento = Documento.DeTrabajador(semilla.Trabajador, semilla.Tipo, Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(1)),
            $"tenant/{Guid.NewGuid():N}.pdf");
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, semilla.Canal);
        propietario.Documentos.Add(documento);
        propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await propietario.SaveChangesAsync();
        return semilla with { Documento = documento.Id, Acreditacion = acreditacion.Id };
    }

    /// <summary>
    /// Una renovación de la ranura anterior: el Documento anterior tenía su Gestión, el nuevo nace con su acreditación y
    /// sin Gestión, y la anterior queda sin marcar.
    /// </summary>
    private async Task<(Sembrado Nuevo, Guid GestionAnterior)> RenovacionSinGestionAsync(
        ArnesDeArranqueRuntime arnes, Guid tenantId, Sembrado semilla)
    {
        var anterior = await ActoSinGestionAsync(arnes, tenantId, semilla);
        using var ambito = AmbitoTenantExplicito.Establecer(tenantId);
        await using var propietario = CrearContextoPropietario(arnes.CadenaPropietario);
        var gestionAnterior = GestionAutomatica.Nacer(anterior.Documento, OrigenGestionAutomatica.AltaDocumento, [anterior.Acreditacion], null)!;
        propietario.GestionesAutomaticas.Add(gestionAnterior);
        await propietario.SaveChangesAsync();

        var documentoAnterior = await propietario.Documentos.SingleAsync(d => d.Id == anterior.Documento);
        var nuevo = documentoAnterior.NuevoDelMismoTitular(Hoy, VigenciaDocumento.VenceEl(Hoy.AddYears(2)), $"tenant/{Guid.NewGuid():N}.pdf", null, null);
        propietario.Documentos.Add(nuevo);
        documentoAnterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
        var acreditacion = new AcreditacionDocumentoPlataforma(nuevo.Id, semilla.Canal);
        propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await propietario.SaveChangesAsync();
        return (semilla with { Documento = nuevo.Id, Acreditacion = acreditacion.Id }, gestionAnterior.Id);
    }

    /// <summary>Lleva el Documento y su acreditación atrás en el tiempo: el acto sigue siendo uno, pero anterior a la ventana.</summary>
    private static async Task EnvejecerAsync(ArnesDeArranqueRuntime arnes, Sembrado sembrado, TimeSpan cuanto)
    {
        await using var propietario = CrearContextoPropietario(arnes.CadenaPropietario);
        var segundos = cuanto.TotalSeconds;
        (await propietario.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Documentos" SET "CreadoEnUtc" = "CreadoEnUtc" - make_interval(secs => {segundos}) WHERE "Id" = {sembrado.Documento}"""))
            .Should().Be(1);
        (await propietario.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "AcreditacionesDocumentoPlataforma" SET "CreadoEnUtc" = "CreadoEnUtc" - make_interval(secs => {segundos}) WHERE "Id" = {sembrado.Acreditacion}"""))
            .Should().Be(1);
    }

    private static CaeManagerDbContext CrearContextoPropietario(string cadenaPropietario)
    {
        var tenant = new TenantDelAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaPropietario)
            .AddInterceptors(new TenantSelladoInterceptor(tenant))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class TenantDelAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class LiderSiempre : IEleccionLiderService
    {
        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            await trabajo(cancellationToken);
            return true;
        }
    }
}
