using CaeManager.Application.Centros.Queries.ObtenerCambiosEstadoPlataformaDeCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.CambiarEstadoAcreditacionEnCentro;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Documentos;

/// <summary>
/// Incremento E1 de la extensión: «Lo he enviado» persiste un cambio de estado con origen Extensión, el Historial del
/// Centro lo devuelve con ese origen, el deshacer de la extensión lo deja deshecho sin borrarlo, y la columna
/// <c>Origen</c> no tiene valor por defecto en la base: quien inserte un cambio tiene que decir de dónde viene.
/// </summary>
public class ConfirmarSubidaDesdeExtensionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Lo_he_enviado_queda_en_el_historial_del_centro_con_origen_extension_y_se_deshace_sin_borrarse()
    {
        var (centroId, acreditacionId) = await SembrarAcreditacionPendienteAsync();

        CambioEstadoAnotadoDto anotado;
        await using (var contexto = CrearContexto())
        {
            var confirmado = await new ConfirmarSubidaDesdeExtensionCommandHandler(
                    new AcreditacionDocumentoPlataformaRepository(contexto), new DocumentoRepository(contexto),
                    new AlcanceDatosServiceFalso(), contexto, contexto, contexto, new CurrentUserServiceFalso(_usuario), contexto)
                .Handle(new ConfirmarSubidaDesdeExtensionCommand(acreditacionId), CancellationToken.None);
            confirmado.EsExitoso.Should().BeTrue(confirmado.EsFallido ? confirmado.Error.Mensaje : null);
            anotado = confirmado.Valor;
        }

        var historial = await LeerHistorialAsync(centroId);
        var fila = historial.Should().ContainSingle().Subject;
        fila.Id.Should().Be(anotado.CambioId);
        fila.Origen.Should().Be(OrigenCambioEstadoAcreditacion.Extension);
        fila.Estado.Should().Be(EstadoAcreditacion.Subida);
        fila.Deshecho.Should().BeFalse();

        await using (var contexto = CrearContexto())
        {
            var deshecho = await new DeshacerSubidaDesdeExtensionCommandHandler(
                    new AcreditacionDocumentoPlataformaRepository(contexto), new DocumentoRepository(contexto),
                    new AlcanceDatosServiceFalso(), contexto, contexto)
                .Handle(new DeshacerSubidaDesdeExtensionCommand(acreditacionId, anotado.CambioId, anotado.Version),
                    CancellationToken.None);
            deshecho.EsExitoso.Should().BeTrue(deshecho.EsFallido ? deshecho.Error.Mensaje : null);
        }

        await using (var lectura = CrearContexto())
        {
            (await lectura.AcreditacionesDocumentoPlataforma.SingleAsync(x => x.Id == acreditacionId))
                .Estado.Should().Be(EstadoAcreditacion.PendienteDeSubir);
        }

        var tras = (await LeerHistorialAsync(centroId)).Should().ContainSingle().Subject;
        tras.Origen.Should().Be(OrigenCambioEstadoAcreditacion.Extension);
        tras.Deshecho.Should().BeTrue("deshacer nunca borra: el cambio sigue en el Historial, marcado");
    }

    [Fact]
    public async Task La_columna_Origen_conserva_el_valor_por_defecto_Centro360_para_la_imagen_anterior()
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var consulta = new NpgsqlCommand(
            """
            SELECT is_nullable, column_default FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'CambiosEstadoAcreditacionPlataforma' AND column_name = 'Origen'
            """, conexion);
        await using var lector = await consulta.ExecuteReaderAsync();

        (await lector.ReadAsync()).Should().BeTrue("la migración AnadeOrigenCambioEstadoAcreditacion crea la columna");
        lector.GetString(0).Should().Be("NO");
        lector.IsDBNull(1).Should().BeFalse(
            "la imagen anterior, durante el relevo azul/verde o tras volver atrás, inserta sin conocer la columna");
        lector.GetString(1).Should().Be("0", "todos los cambios de la imagen anterior salen de Centro 360 (0)");
    }

    private async Task<IReadOnlyList<CambioEstadoPlataformaCentroDto>> LeerHistorialAsync(Guid centroId)
    {
        await using var contexto = CrearContexto();
        return await new ObtenerCambiosEstadoPlataformaDeCentroQueryHandler(
                contexto, contexto, contexto, contexto, contexto, contexto, contexto,
                new AlcanceDatosServiceFalso(), new DirectorioVacio(), new CurrentUserServiceFalso(_usuario))
            .Handle(new ObtenerCambiosEstadoPlataformaDeCentroQuery(centroId), CancellationToken.None);
    }

    private async Task<(Guid CentroId, Guid AcreditacionId)> SembrarAcreditacionPendienteAsync()
    {
        await using var contexto = CrearContexto();
        var titular = Empresa.CrearComoCliente("Titular E1", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var proveedora = new Empresa("Contratista E1", "B10380202");
        var centro = new Centro(titular.Id, proveedora.Id, "Almacén Norte");
        var proveedor = new ProveedorPlataformaCae($"prueba-{Guid.NewGuid():N}", "Portal de prueba");
        var acceso = CanalGestionDocumental.DePlataforma(centro.Id, "Portal", proveedor.Id, null, null, null);
        var tipo = new TipoDocumento("Formación PRL", 12, aplicaVencimientoAutomatico: true, orden: 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
        var trabajador = Trabajador.DeEmpresa(proveedora.Id, "Ana", "Pérez", "12345678Z");
        var hoy = DiaDeNegocio.Hoy();
        var documento = Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(5)));
        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, acceso.Id);
        contexto.Empresas.AddRange(titular, proveedora);
        contexto.Centros.Add(centro);
        contexto.ProveedoresPlataformaCae.Add(proveedor);
        contexto.CanalesGestionDocumental.Add(acceso);
        contexto.TiposDocumento.Add(tipo);
        contexto.Trabajadores.Add(trabajador);
        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy.AddDays(-1)));
        contexto.Documentos.Add(documento);
        contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await contexto.SaveChangesAsync();
        return (centro.Id, acreditacion.Id);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new AuditoriaInterceptor(new ActorAuditoriaFalso(ActorAuditoria.Normal(_usuario))),
                new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class DirectorioVacio : IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(
            IReadOnlyCollection<Guid> usuarioIds, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

        public Task<bool> EsCuentaActivaConRolAsync(
            Guid usuarioId, Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(
            Guid tenantId, string rol, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Guid>>([]);

        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid usuarioId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Guid?>(null);
    }
}
