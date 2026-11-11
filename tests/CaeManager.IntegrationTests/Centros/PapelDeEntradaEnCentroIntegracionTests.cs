using CaeManager.Application.Centros.Commands.EstablecerRelacionDeEntradaCentro;
using CaeManager.Application.Centros.Queries;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.EstadoMaterializado;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// Papel por Centro (DECISION_LOG, 2026-11-08): el ancla <c>Centros.RelacionEmpresarialEntradaId</c> se guarda
/// y se relee en el mismo Tenant propietario, y la FK compuesta con <c>TenantId</c> rechaza en la base una
/// Relación Empresarial de otro Tenant aunque se salte el dominio. Mismo patrón que
/// <see cref="RelacionEmpresarialAciclicidadTests"/>.
/// </summary>
public class PapelDeEntradaEnCentroIntegracionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_ancla_de_una_relacion_del_mismo_Tenant_se_guarda_y_se_relee()
    {
        var (centroId, relacionId) = await SembrarCentroConRelacionAsync(_tenant);

        await using (var contexto = CrearContexto(_tenant))
        {
            var centro = await contexto.Centros.SingleAsync(c => c.Id == centroId);
            var relacion = await contexto.RelacionesEmpresariales.SingleAsync(r => r.Id == relacionId);
            centro.AnclarRelacionDeEntrada(relacion).EsExitoso.Should().BeTrue();
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto(_tenant);
        (await lectura.Centros.SingleAsync(c => c.Id == centroId)).RelacionEmpresarialEntradaId.Should().Be(relacionId);
    }

    [Fact]
    public async Task La_base_rechaza_un_ancla_hacia_una_relacion_de_otro_Tenant()
    {
        var (centroId, _) = await SembrarCentroConRelacionAsync(_tenant);
        var (_, relacionAjenaId) = await SembrarCentroConRelacionAsync(_otroTenant);

        await using var contexto = CrearContexto(_tenant);
        var actualizar = () => contexto.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Centros" SET "RelacionEmpresarialEntradaId" = {relacionAjenaId} WHERE "Id" = {centroId}""");

        (await actualizar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    /// <summary>
    /// La ficha del Centro lee el ancla y su X con LEFT JOIN en su propia sentencia, contra PostgreSQL: con X
    /// visible nombra a X; con X fuera de la cartera de quien mira, el papel es igual que el de un Centro sin ancla.
    /// </summary>
    [Fact]
    public async Task La_ficha_lee_el_papel_del_ancla_y_no_lo_revela_con_X_fuera_de_cartera()
    {
        var anclado = await SembrarAsync(_tenant);
        var sinAncla = await SembrarAsync(_tenant);
        await using (var contexto = CrearContexto(_tenant))
        {
            var centro = await contexto.Centros.SingleAsync(c => c.Id == anclado.CentroId);
            var relacion = await contexto.RelacionesEmpresariales.SingleAsync(r => r.Id == anclado.RelacionId);
            centro.AnclarRelacionDeEntrada(relacion).EsExitoso.Should().BeTrue();
            await contexto.SaveChangesAsync();
        }

        (await PapelEnLaFichaAsync(anclado.CentroId, new AlcanceDatosServiceFalso()))
            .Should().Be(new PapelDeEntradaDto(true, anclado.ContratistaPrincipalId, anclado.ContratistaPrincipalRazonSocial));

        var sinCarteraSobreX = (Guid centroId, Guid titularId) => new AlcanceDatosServiceFalso(
            clienteIds: [titularId], centroIds: [centroId], centroIdsParaGestion: [centroId]);
        var conAncla = await PapelEnLaFichaAsync(anclado.CentroId, sinCarteraSobreX(anclado.CentroId, anclado.TitularId));
        var gemelo = await PapelEnLaFichaAsync(sinAncla.CentroId, sinCarteraSobreX(sinAncla.CentroId, sinAncla.TitularId));
        conAncla.Should().Be(gemelo);
        conAncla.Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
    }

    /// <summary>
    /// La arista anclada se cierra en la base después de fijar el ancla: la ficha deja de dar papel (DECISION_LOG,
    /// 2026-11-11, punto 11 b), igual que la de un Centro gemelo sin ancla, y el ancla sigue guardada.
    /// </summary>
    [Fact]
    public async Task La_ficha_oculta_el_papel_si_la_arista_anclada_se_cierra()
    {
        var anclado = await SembrarAsync(_tenant);
        var sinAncla = await SembrarAsync(_tenant);
        await using (var contexto = CrearContexto(_tenant))
        {
            var centro = await contexto.Centros.SingleAsync(c => c.Id == anclado.CentroId);
            var relacion = await contexto.RelacionesEmpresariales.SingleAsync(r => r.Id == anclado.RelacionId);
            centro.AnclarRelacionDeEntrada(relacion).EsExitoso.Should().BeTrue();
            await contexto.SaveChangesAsync();
        }

        (await PapelEnLaFichaAsync(anclado.CentroId, new AlcanceDatosServiceFalso()))
            .Should().Be(new PapelDeEntradaDto(true, anclado.ContratistaPrincipalId, anclado.ContratistaPrincipalRazonSocial),
                "antes de cerrarla la arista vigente sí da papel");

        await using (var contexto = CrearContexto(_tenant))
        {
            var relacion = await contexto.RelacionesEmpresariales.SingleAsync(r => r.Id == anclado.RelacionId);
            relacion.Cerrar(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var conAnclaCerrada = await PapelEnLaFichaAsync(anclado.CentroId, new AlcanceDatosServiceFalso());
        var gemelo = await PapelEnLaFichaAsync(sinAncla.CentroId, new AlcanceDatosServiceFalso());
        conAnclaCerrada.Should().Be(gemelo);
        conAnclaCerrada.Should().Be(PapelDeEntradaDto.ContratistaPrincipal);

        await using var lectura = CrearContexto(_tenant);
        (await lectura.Centros.SingleAsync(c => c.Id == anclado.CentroId))
            .RelacionEmpresarialEntradaId.Should().Be(anclado.RelacionId);
    }

    /// <summary>
    /// Quitar un ancla cuya arista se cerró no exige ver a X (DECISION_LOG, 2026-11-11, punto 11 b). Un Gestor CAE
    /// sin Asignación de Cartera sobre X lee la ficha, envía el comando sin relación y relee, en un Centro con ancla
    /// cerrada y en un gemelo sin ancla: con PostgreSQL, el repositorio y los interceptores de auditoría y de
    /// concurrencia reales, la respuesta, el cambio de versión, el papel, las filas y la acción de auditoría y las
    /// raíces que el guardado entrega al recálculo del estado son iguales en los dos. Si quitar sin ancla no renovase la
    /// versión, esta comparación distinguiría los dos Centros.
    /// </summary>
    [Fact]
    public async Task Quitar_un_ancla_cerrada_tiene_el_mismo_efecto_observable_que_quitar_en_un_Centro_sin_ancla()
    {
        var anclado = await SembrarAsync(_tenant);
        var sinAncla = await SembrarAsync(_tenant);
        await using (var contexto = CrearContexto(_tenant))
        {
            var centro = await contexto.Centros.SingleAsync(c => c.Id == anclado.CentroId);
            var relacion = await contexto.RelacionesEmpresariales.SingleAsync(r => r.Id == anclado.RelacionId);
            centro.AnclarRelacionDeEntrada(relacion).EsExitoso.Should().BeTrue();
            relacion.Cerrar(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var conAnclaCerrada = await QuitarYObservarAsync(anclado);
        var gemelo = await QuitarYObservarAsync(sinAncla);

        conAnclaCerrada.Should().Be(gemelo);
        conAnclaCerrada.Exito.Should().BeTrue();
        conAnclaCerrada.CodigoError.Should().BeNull();
        conAnclaCerrada.VersionCambio.Should().BeTrue();
        conAnclaCerrada.Papel.Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
        conAnclaCerrada.FilasDeAuditoria.Should().Be(1);
        conAnclaCerrada.AccionesAuditadas.Should().NotBeNullOrEmpty();
        conAnclaCerrada.RecalculoDeEstado.Should().Be(
            "tenant=propio;disparadores=Centro;centros=Centro;afectados=;empresas=EmpresaDelCentro;trabajadores=0;eventos=0;tenantEntero=",
            "quitar el ancla recalcula el estado del Centro y de su Empresa, como cualquier cambio del Centro");

        await using var lectura = CrearContexto(_tenant);
        (await lectura.Centros.SingleAsync(c => c.Id == anclado.CentroId))
            .RelacionEmpresarialEntradaId.Should().BeNull("el ancla cerrada sí se borró");
    }

    /// <summary>
    /// Lo que distingue un guardado de otro para quien envía el comando y para lo que se dispara detrás: respuesta,
    /// versión, papel, filas y acción de auditoría, y las raíces que el guardado entrega al recálculo del estado
    /// materializado. El comando no publica eventos ni notificaciones (no tiene publicador y el Centro no levanta
    /// eventos de dominio); los avisos del estado salen de esas mismas raíces tras el COMMIT. Los identificadores se
    /// traducen a su papel en el sembrado (<c>Centro</c>, <c>EmpresaDelCentro</c>…) para comparar dos Centros gemelos.
    /// </summary>
    private sealed record EfectoObservable(
        bool Exito, string? CodigoError, bool VersionCambio, PapelDeEntradaDto? Papel, int FilasDeAuditoria,
        string AccionesAuditadas, string RecalculoDeEstado);

    private async Task<EfectoObservable> QuitarYObservarAsync(Sembrado sembrado)
    {
        var sinCarteraSobreX = new AlcanceDatosServiceFalso(
            clienteIds: [sembrado.TitularId], centroIds: [sembrado.CentroId], centroIdsParaGestion: [sembrado.CentroId]);
        var antes = await FichaAsync(sembrado.CentroId, sinCarteraSobreX);
        var auditoriaAntes = await AuditoriaAsync(sembrado.CentroId);

        Result resultado;
        var raices = new RaicesDeRecalculoQueAnota();
        await using (var contexto = CrearContextoConInterceptoresDeProduccion(raices))
        {
            resultado = await new EstablecerRelacionDeEntradaCentroCommandHandler(
                    new CentroRepository(contexto), contexto, sinCarteraSobreX, contexto)
                .Handle(new EstablecerRelacionDeEntradaCentroCommand(sembrado.CentroId, null, antes.Version),
                    CancellationToken.None);
        }

        var despues = await FichaAsync(sembrado.CentroId, sinCarteraSobreX);
        var nuevas = (await AuditoriaAsync(sembrado.CentroId)).Skip(auditoriaAntes.Count).ToList();
        return new EfectoObservable(
            resultado.EsExitoso,
            resultado.EsExitoso ? null : resultado.Error.Codigo,
            despues.Version != antes.Version,
            despues.PapelDeEntrada,
            nuevas.Count,
            string.Join("|", nuevas),
            DescribirRaices(raices.Cambios, sembrado));
    }

    /// <summary>Entidad y acción de cada fila de auditoría del Centro, en orden de escritura.</summary>
    private async Task<List<string>> AuditoriaAsync(Guid centroId)
    {
        await using var contexto = CrearContexto(_tenant);
        return await contexto.RegistrosAuditoria.IgnoreQueryFilters()
            .Where(r => r.EntidadId == centroId)
            .OrderBy(r => r.FechaUtc).ThenBy(r => r.Id)
            .Select(r => r.EntidadTipo + ":" + r.Accion)
            .ToListAsync();
    }

    private string DescribirRaices(CambiosEstadoDetectados cambios, Sembrado s)
    {
        string Papel(Guid id) =>
            id == s.CentroId ? "Centro"
            : id == s.EmpresaDelCentroId ? "EmpresaDelCentro"
            : id == s.TitularId ? "Titular"
            : id == s.ContratistaPrincipalId ? "X"
            : "otra";
        string Lista(IEnumerable<Guid> ids) => string.Join(",", ids.Select(Papel).Order(StringComparer.Ordinal));

        return string.Join(" / ", cambios.PorTenant.OrderBy(p => p.Key).Select(p =>
            $"tenant={(p.Key == _tenant ? "propio" : "otro")};disparadores={string.Join(",", p.Value.Disparadores)};" +
            $"centros={Lista(p.Value.Centros)};afectados={Lista(p.Value.CentrosAfectados)};" +
            $"empresas={Lista(p.Value.Empresas)};trabajadores={p.Value.Trabajadores.Count};" +
            $"eventos={p.Value.Eventos.Count};tenantEntero={p.Value.TenantEntero}"));
    }

    /// <summary>
    /// Anota, con el mismo inventario que <see cref="DetectorCambiosEstadoInterceptor"/> y en el mismo momento (al
    /// guardar, después del sellado de Tenant), las raíces que el guardado entregaría al recálculo del estado. No
    /// recalcula: el recálculo es una función de esas raíces y de la base, que es igual en los dos gemelos.
    /// </summary>
    private sealed class RaicesDeRecalculoQueAnota : SaveChangesInterceptor
    {
        public CambiosEstadoDetectados Cambios { get; } = new();

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            foreach (var entrada in eventData.Context!.ChangeTracker.Entries())
                InventarioDisparadoresEstado.Recoger(entrada, Cambios);
            return ValueTask.FromResult(result);
        }
    }

    private async Task<CentroDetalleDto> FichaAsync(Guid centroId, AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto(_tenant);
        var dto = await new ObtenerCentroPorIdQueryHandler(contexto, contexto, alcance, PoliticaNotaInternaPruebas.Con("GestorCae"))
            .Handle(new ObtenerCentroPorIdQuery(centroId), CancellationToken.None);
        dto.Should().NotBeNull();
        return dto!;
    }

    private async Task<PapelDeEntradaDto?> PapelEnLaFichaAsync(Guid centroId, AlcanceDatosServiceFalso alcance) =>
        (await FichaAsync(centroId, alcance)).PapelDeEntrada;

    private sealed record Sembrado(
        Guid CentroId, Guid RelacionId, Guid TitularId, Guid ContratistaPrincipalId, string ContratistaPrincipalRazonSocial,
        Guid EmpresaDelCentroId);

    private async Task<(Guid CentroId, Guid RelacionId)> SembrarCentroConRelacionAsync(Guid tenant)
    {
        var sembrado = await SembrarAsync(tenant);
        return (sembrado.CentroId, sembrado.RelacionId);
    }

    private async Task<Sembrado> SembrarAsync(Guid tenant)
    {
        await using var contexto = CrearContexto(tenant);
        var sufijo = Guid.NewGuid().ToString("N");
        var titular = new Empresa($"Titular {sufijo}");
        var empresaDelCentro = new Empresa($"Empresa del Centro {sufijo}");
        var contratistaPrincipal = new Empresa($"Contratista principal {sufijo}");
        contexto.Empresas.AddRange(titular, empresaDelCentro, contratistaPrincipal);
        await contexto.SaveChangesAsync();

        var relacion = RelacionEmpresarial.Crear(empresaDelCentro.Id, contratistaPrincipal.Id, DateTime.UtcNow);
        var centro = new Centro(titular.Id, empresaDelCentro.Id, $"Centro {sufijo}");
        contexto.RelacionesEmpresariales.Add(relacion);
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();
        return new Sembrado(
            centro.Id, relacion.Id, titular.Id, contratistaPrincipal.Id, contratistaPrincipal.RazonSocial, empresaDelCentro.Id);
    }

    private CaeManagerDbContext CrearContexto(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Con los interceptores que renuevan la versión y escriben la auditoría, como en producción (mismo montaje que
    /// <c>ReactivarVisitaConcurrenciaTests</c>): sin <see cref="ConcurrenciaOptimistaInterceptor"/> la versión de un
    /// Centro modificado no cambiaría y la comparación no observaría nada.
    /// </summary>
    private CaeManagerDbContext CrearContextoConInterceptoresDeProduccion(RaicesDeRecalculoQueAnota raices)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var actor = new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()));
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            // Mismo orden que ConfiguracionDeContexto.Aplicar; las raíces se anotan las últimas, como el detector.
            .AddInterceptors(new AuditoriaInterceptor(actor), new TenantSelladoInterceptor(tenantActual),
                new ConcurrenciaOptimistaInterceptor(), raices)
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
