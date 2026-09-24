using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Incremento 1 de PROPUESTA-BUZONES-COMPARTIDOS-M365 § 5.4, capa
/// Infrastructure/PostgreSQL con RLS real: la propiedad que de verdad cierra
/// el bug (mismo BuzonEmail conectado en dos Tenants a la vez) solo la
/// garantiza el índice único de <see cref="ReclamacionBuzonIntegracion"/>
/// contra PostgreSQL real, no un doble en memoria — mismo criterio que
/// <c>AislamientoSolicitudConexionMicrosoft365Tests</c>.
/// </summary>
public class ReclamacionBuzonIntegracionRepositoryGuardaUnicidadTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var dbContext = CrearContexto(_tenantA);
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    [Fact]
    public async Task Un_tenant_no_puede_reclamar_un_buzon_ya_conectado_por_otro_y_no_deja_filas_huerfanas()
    {
        Guid conexionAId;
        await using (var contextoA = CrearContexto(_tenantA))
        {
            var conexionA = new ConexionIntegracion("cae@arcosspa.com", "Buzón CAE");
            contextoA.ConexionesIntegracion.Add(conexionA);
            new ReclamacionBuzonIntegracionRepository(contextoA)
                .Reclamar(new ReclamacionBuzonIntegracion(conexionA.BuzonEmail, _tenantA, conexionA.Id, DateTime.UtcNow));
            await contextoA.SaveChangesAsync();
            conexionAId = conexionA.Id;
        }

        await using var contextoB = CrearContexto(_tenantB);
        var conexionB = new ConexionIntegracion("cae@arcosspa.com", "Buzón CAE (intento del tenant B)");
        contextoB.ConexionesIntegracion.Add(conexionB);
        var reclamacionRepositorioB = new ReclamacionBuzonIntegracionRepository(contextoB);
        reclamacionRepositorioB.Reclamar(
            new ReclamacionBuzonIntegracion(conexionB.BuzonEmail, _tenantB, conexionB.Id, DateTime.UtcNow));

        var buzonLibre = await reclamacionRepositorioB.GuardarCambiosSiBuzonLibreAsync();

        buzonLibre.Should().BeFalse("el buzón ya está reclamado por el tenant A");

        // Sin fila huérfana: ni la ConexionIntegracion ni la reclamación del
        // tenant B llegaron a persistirse — el ChangeTracker.Clear() del
        // repositorio deshace TODO el Unit of Work compartido, no solo la
        // reclamación.
        await using var contextoVerificacionB = CrearContexto(_tenantB);
        (await contextoVerificacionB.ConexionesIntegracion.AnyAsync(c => c.Id == conexionB.Id)).Should().BeFalse(
            "un SaveChangesAsync rechazado no debe dejar la ConexionIntegracion del intento perdedor a medio guardar");

        // La reclamación original del tenant A sigue intacta.
        await using var contextoVerificacionGlobal = CrearContexto(null);
        var reclamacionesDelBuzon = await contextoVerificacionGlobal.ReclamacionesBuzonIntegracion
            .Where(r => r.BuzonEmail == "cae@arcosspa.com")
            .ToListAsync();
        reclamacionesDelBuzon.Should().ContainSingle().Which.ConexionIntegracionId.Should().Be(conexionAId);
    }

    [Fact]
    public async Task Dos_tenants_distintos_pueden_reclamar_cada_uno_su_propio_buzon()
    {
        await using var contextoA = CrearContexto(_tenantA);
        var conexionA = new ConexionIntegracion("cae@arcosspa.com", "Buzón CAE");
        contextoA.ConexionesIntegracion.Add(conexionA);
        var reclamacionRepositorioA = new ReclamacionBuzonIntegracionRepository(contextoA);
        reclamacionRepositorioA.Reclamar(new ReclamacionBuzonIntegracion(conexionA.BuzonEmail, _tenantA, conexionA.Id, DateTime.UtcNow));

        (await reclamacionRepositorioA.GuardarCambiosSiBuzonLibreAsync()).Should().BeTrue();

        await using var contextoB = CrearContexto(_tenantB);
        var conexionB = new ConexionIntegracion("cae@refrielectric.com", "Buzón CAE");
        contextoB.ConexionesIntegracion.Add(conexionB);
        var reclamacionRepositorioB = new ReclamacionBuzonIntegracionRepository(contextoB);
        reclamacionRepositorioB.Reclamar(new ReclamacionBuzonIntegracion(conexionB.BuzonEmail, _tenantB, conexionB.Id, DateTime.UtcNow));

        (await reclamacionRepositorioB.GuardarCambiosSiBuzonLibreAsync()).Should().BeTrue(
            "son dos buzones distintos, la guarda de unicidad no debe interferir entre ellos");
    }

    /// <summary>
    /// Punto 3 de la revisión Codex sobre PR #820: el catch genérico de
    /// <see cref="ReclamacionBuzonIntegracionRepository.GuardarCambiosSiBuzonLibreAsync"/>
    /// debe limpiar el <c>ChangeTracker</c> también cuando el
    /// <see cref="DbUpdateException"/> viene de OTRO índice único ajeno al
    /// buzón (aquí, (TenantId, Nombre) de ConexionIntegracion) — y relanzar,
    /// no tragarse el fallo real del plan.
    /// </summary>
    [Fact]
    public async Task Un_fallo_de_guardado_ajeno_al_buzon_se_relanza_y_limpia_el_change_tracker()
    {
        await using (var contextoPrevio = CrearContexto(_tenantA))
        {
            contextoPrevio.ConexionesIntegracion.Add(new ConexionIntegracion("cae@arcosspa.com", "Buzón CAE"));
            await contextoPrevio.SaveChangesAsync();
        }

        await using var contexto = CrearContexto(_tenantA);
        var conexionConNombreDuplicado = new ConexionIntegracion("otro-buzon@arcosspa.com", "Buzón CAE");
        contexto.ConexionesIntegracion.Add(conexionConNombreDuplicado);
        var reclamacionRepositorio = new ReclamacionBuzonIntegracionRepository(contexto);
        reclamacionRepositorio.Reclamar(
            new ReclamacionBuzonIntegracion(conexionConNombreDuplicado.BuzonEmail, _tenantA, conexionConNombreDuplicado.Id, DateTime.UtcNow));

        await FluentActions.Awaiting(() => reclamacionRepositorio.GuardarCambiosSiBuzonLibreAsync())
            .Should().ThrowAsync<DbUpdateException>("(TenantId, Nombre) es un índice único ajeno al de buzón y debe propagarse, no tragarse");

        // El mismo DbContext (scoped por circuito Blazor) debe quedar limpio:
        // una operación válida posterior en el mismo contexto no debe
        // arrastrar la ConexionIntegracion/ReclamacionBuzonIntegracion del
        // intento fallido.
        var conexionValidaPosterior = new ConexionIntegracion("valida-despues@arcosspa.com", "Otro nombre distinto");
        contexto.ConexionesIntegracion.Add(conexionValidaPosterior);
        var reclamacionRepositorioPosterior = new ReclamacionBuzonIntegracionRepository(contexto);
        reclamacionRepositorioPosterior.Reclamar(
            new ReclamacionBuzonIntegracion(conexionValidaPosterior.BuzonEmail, _tenantA, conexionValidaPosterior.Id, DateTime.UtcNow));

        (await reclamacionRepositorioPosterior.GuardarCambiosSiBuzonLibreAsync()).Should().BeTrue(
            "sin el Clear(), la ConexionIntegracion del intento fallido seguiría trackeada como Added y este SaveChangesAsync fallaría también");

        await using var contextoVerificacion = CrearContexto(_tenantA);
        (await contextoVerificacion.ConexionesIntegracion.AnyAsync(c => c.Id == conexionConNombreDuplicado.Id)).Should().BeFalse(
            "la conexión del intento fallido no debe haberse persistido");
    }
}
