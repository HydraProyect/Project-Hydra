using System.IO.Compression;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Application.Visitas.Queries.ObtenerDetalleVisita;
using CaeManager.Application.Visitas.Queries.ObtenerPaqueteDocumentalVisita;
using CaeManager.Application.Visitas.Queries.ObtenerSolicitudAccesoCorreo;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// P1-X1: Centro gestionado por correo. El texto de la solicitud de acceso y el zip para
/// descargar, bajo <c>cae_app_runtime</c> con RLS efectiva y el <see cref="AlcanceDatosService"/>
/// real — la cartera sale de una <see cref="AsignacionCartera"/> de verdad, no de un fake.
///
/// <para>
/// Escenario, Tenant propietario A:
/// <list type="bullet">
/// <item>Cliente empresarial dentro (en la cartera de la Gestora CAE) con el Centro «Correo
/// dentro», cuyo canal principal es el correo, y el Centro «Sin canal», sin ningún canal.</item>
/// <item>Cliente empresarial fuera (sin cartera) con el Centro «Correo fuera», también por
/// correo.</item>
/// <item>Una Trabajadora de la Empresa propia con un reconocimiento médico vigente (categoría
/// especial de salud), otro vencido y la póliza de la Empresa (sin datos personales).</item>
/// </list>
/// El Tenant B existe solo para pedir desde él.
/// </para>
///
/// <para>
/// Los registros de acceso sensible se leen como propietario, sin RLS ni filtro de EF: la
/// pregunta es «qué quedó escrito», y leerlo con el filtro que podría esconderlo convertiría
/// una fila ausente en un verde.
/// </para>
/// </summary>
public class PaqueteDocumentalVisitaCorreoTests
{
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _gestora = Guid.NewGuid();

    private readonly TenantActualAmbiental _tenant = new();
    private readonly UsuarioConmutable _usuario = new();
    private readonly AlmacenamientoEnMemoria _almacenamiento = new();

    private Guid _visitaDentro;
    private Guid _visitaFuera;
    private Guid _visitaSinCanal;
    private Guid _reconocimientoVigente;
    private Guid _reconocimientoVencido;
    private Guid _poliza;

    [Fact]
    public async Task La_Gestora_descarga_solo_lo_vigente_y_queda_registrado_el_acceso_al_documento_sensible()
    {
        await using var arnes = await PrepararAsync();

        var resultado = await DescargarAsync(arnes, _gestora, "GestorCae", _tenantA, _visitaDentro);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        resultado.Valor.NombreArchivo.Should().EndWith(".zip");
        LeerZip(resultado.Valor.Contenido).Should().BeEquivalentTo(["reconocimiento-vigente", "poliza"],
            "solo vigentes, uno por titular y tipo, nunca el vencido");

        var registros = await LeerRegistrosSensiblesAsync(arnes);
        registros.Should().ContainSingle("la póliza no tiene datos personales y el vencido no viajó (DEC-36: nunca registrar de más)")
            .Which.Should().Be((_reconocimientoVigente, (Guid?)_gestora, _tenantA));
    }

    [Fact]
    public async Task La_Gestora_no_descarga_la_Visita_de_un_Centro_fuera_de_su_cartera_ni_deja_registro()
    {
        await using var arnes = await PrepararAsync();

        var resultado = await DescargarAsync(arnes, _gestora, "GestorCae", _tenantA, _visitaFuera);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(ObtenerSolicitudAccesoCorreoQueryHandler.NoEncontrada,
            "fuera de cartera se responde igual que si no existiera");
        (await LeerRegistrosSensiblesAsync(arnes)).Should().BeEmpty("sin contenido entregado no hay acceso que registrar");
    }

    [Fact]
    public async Task Desde_otro_Tenant_la_Visita_no_existe_ni_para_un_Administrador()
    {
        await using var arnes = await PrepararAsync();

        var resultado = await DescargarAsync(arnes, Guid.NewGuid(), "Administrador", _tenantB, _visitaDentro);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Should().Be(ObtenerSolicitudAccesoCorreoQueryHandler.NoEncontrada);
        (await LeerRegistrosSensiblesAsync(arnes)).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Centro_que_no_se_gestiona_por_correo_no_ofrece_zip_ni_solicitud()
    {
        await using var arnes = await PrepararAsync();

        var descarga = await DescargarAsync(arnes, _gestora, "GestorCae", _tenantA, _visitaSinCanal);
        var solicitud = await SolicitudAsync(arnes, _gestora, "GestorCae", _tenantA, _visitaSinCanal);

        descarga.Error.Should().Be(ObtenerSolicitudAccesoCorreoQueryHandler.CentroNoGestionadoPorCorreo);
        solicitud.Error.Should().Be(ObtenerSolicitudAccesoCorreoQueryHandler.CentroNoGestionadoPorCorreo);
        (await LeerRegistrosSensiblesAsync(arnes)).Should().BeEmpty();
    }

    [Fact]
    public async Task La_solicitud_va_al_correo_del_Centro_y_no_lleva_el_DNI()
    {
        await using var arnes = await PrepararAsync();

        var solicitud = await SolicitudAsync(arnes, _gestora, "GestorCae", _tenantA, _visitaDentro);

        solicitud.EsExitoso.Should().BeTrue(solicitud.EsFallido ? solicitud.Error.Codigo : "");
        solicitud.Valor.Destinatarios.Should().Be("accesos@centro-correo.es");
        solicitud.Valor.Cuerpo.Should().StartWith("Buenos días, Marta:");
        solicitud.Valor.Cuerpo.Should().Contain("- Ana Garcia (Empresa propia)");
        solicitud.Valor.Cuerpo.Should().NotContain("12345678Z", "el DNI no viaja en el correo (#856)");

        (await SolicitudAsync(arnes, _gestora, "GestorCae", _tenantA, _visitaFuera)).Error
            .Should().Be(ObtenerSolicitudAccesoCorreoQueryHandler.NoEncontrada);
    }

    [Fact]
    public async Task El_detalle_de_la_Visita_solo_marca_como_gestionado_por_correo_el_Centro_cuyo_canal_es_el_correo()
    {
        await using var arnes = await PrepararAsync();

        (await DetalleAsync(arnes, _visitaDentro))!.CentroGestionadoPorCorreo.Should().BeTrue();
        (await DetalleAsync(arnes, _visitaSinCanal))!.CentroGestionadoPorCorreo.Should().BeFalse();
    }

    private async Task<ArnesDeArranqueRuntime> PrepararAsync()
    {
        var arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: _tenant,
            actorAuditoriaPersonalizado: _usuario,
            currentUserServicePersonalizado: _usuario);

        Como(_gestora, "GestorCae", _tenantA);
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var clienteDentro = Empresa.CrearComoCliente("Cliente empresarial dentro", "B10380186", false, null, null);
        var clienteFuera = Empresa.CrearComoCliente("Cliente empresarial fuera", "B10380194", false, null, null);
        var propia = new Empresa("Empresa propia", "B10380202");
        contexto.Empresas.AddRange(clienteDentro, clienteFuera, propia);

        var centroDentro = new Centro(clienteDentro.Id, propia.Id, "Correo dentro");
        var centroSinCanal = new Centro(clienteDentro.Id, propia.Id, "Sin canal");
        var centroFuera = new Centro(clienteFuera.Id, propia.Id, "Correo fuera");
        contexto.Centros.AddRange(centroDentro, centroSinCanal, centroFuera);

        var canalDentro = CanalGestionDocumental.PorEmail(centroDentro.Id, "Gestión general", "accesos@centro-correo.es", "Marta");
        canalDentro.MarcarComoPrincipal();
        var canalFuera = CanalGestionDocumental.PorEmail(centroFuera.Id, "Gestión general", "accesos@fuera.es", null);
        canalFuera.MarcarComoPrincipal();
        contexto.CanalesGestionDocumental.AddRange(canalDentro, canalFuera);

        var ana = Trabajador.DeEmpresa(propia.Id, "Ana", "Garcia", "12345678Z");
        contexto.Trabajadores.Add(ana);

        var reconocimiento = new TipoDocumento("Reconocimiento médico", null, false, 1, AmbitoAplicacion.Trabajador,
            sensibilidad: SensibilidadDocumental.CategoriaEspecialSalud);
        var polizaTipo = new TipoDocumento("Póliza RC", null, false, 2, AmbitoAplicacion.Empresa,
            sensibilidad: SensibilidadDocumental.SinDatosPersonales);
        contexto.TiposDocumento.AddRange(reconocimiento, polizaTipo);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var vigente = Documento.DeTrabajador(ana.Id, reconocimiento.Id, hoy.AddDays(-30),
            VigenciaDocumento.VenceEl(hoy.AddDays(300)), await GuardarAsync("reconocimiento-vigente"));
        var vencido = Documento.DeTrabajador(ana.Id, reconocimiento.Id, hoy.AddDays(-500),
            VigenciaDocumento.VenceEl(hoy.AddDays(-10)), await GuardarAsync("reconocimiento-vencido"));
        var poliza = Documento.DeEmpresa(propia.Id, polizaTipo.Id, hoy.AddDays(-30),
            VigenciaDocumento.VenceEl(hoy.AddDays(200)), await GuardarAsync("poliza"));
        contexto.Documentos.AddRange(vigente, vencido, poliza);

        var manana = hoy.AddDays(1);
        var visitaDentro = new Visita(centroDentro.Id, manana, manana, "nota interna");
        var visitaSinCanal = new Visita(centroSinCanal.Id, manana, manana, null);
        var visitaFuera = new Visita(centroFuera.Id, manana, manana, null);
        contexto.Visitas.AddRange(visitaDentro, visitaSinCanal, visitaFuera);
        await contexto.SaveChangesAsync();

        contexto.VisitasTrabajadores.AddRange(
            new VisitaTrabajador(visitaDentro.Id, ana.Id),
            new VisitaTrabajador(visitaSinCanal.Id, ana.Id),
            new VisitaTrabajador(visitaFuera.Id, ana.Id));

        var ahora = DateTime.UtcNow;
        var raiz = AsignacionOperacion.Raiz(_tenantA, ServicioCae.Outbound, ahora, ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, _gestora, AmbitoAsignacion.DeRelacionCliente(clienteDentro.Id), ahora, null, ahora));
        await contexto.SaveChangesAsync();

        (_visitaDentro, _visitaFuera, _visitaSinCanal) = (visitaDentro.Id, visitaFuera.Id, visitaSinCanal.Id);
        (_reconocimientoVigente, _reconocimientoVencido, _poliza) = (vigente.Id, vencido.Id, poliza.Id);
        return arnes;
    }

    private void Como(Guid usuarioId, string rol, Guid tenantId)
    {
        _usuario.UsuarioId = usuarioId;
        _usuario.Rol = rol;
        _usuario.TenantOrigenId = tenantId;
        _tenant.TenantId = tenantId;
    }

    private async Task<Result<PaqueteDocumentalDescargaDto>> DescargarAsync(
        ArnesDeArranqueRuntime arnes, Guid usuarioId, string rol, Guid tenantId, Guid visitaId)
    {
        Como(usuarioId, rol, tenantId);
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var paquete = new PaqueteDocumentalVisitaService(
            contexto, contexto, contexto, contexto, contexto, contexto, new ConversacionRepository(contexto), _almacenamiento,
            NullLogger<PaqueteDocumentalVisitaService>.Instance);
        var registro = new RegistroAccesoDocumentoSensibleService(
            contexto, contexto, _usuario,
            new RegistroAccesoDocumentoSensibleRepository(contexto, NullLogger<RegistroAccesoDocumentoSensibleRepository>.Instance));

        var handler = new ObtenerPaqueteDocumentalVisitaQueryHandler(contexto, contexto, Alcance(contexto), paquete, registro);
        return await handler.Handle(new ObtenerPaqueteDocumentalVisitaQuery(visitaId), CancellationToken.None);
    }

    private async Task<Result<SolicitudAccesoCorreoDto>> SolicitudAsync(
        ArnesDeArranqueRuntime arnes, Guid usuarioId, string rol, Guid tenantId, Guid visitaId)
    {
        Como(usuarioId, rol, tenantId);
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var handler = new ObtenerSolicitudAccesoCorreoQueryHandler(contexto, contexto, contexto, contexto, Alcance(contexto));
        return await handler.Handle(new ObtenerSolicitudAccesoCorreoQuery(visitaId), CancellationToken.None);
    }

    private async Task<DetalleVisitaDto?> DetalleAsync(ArnesDeArranqueRuntime arnes, Guid visitaId)
    {
        Como(_gestora, "GestorCae", _tenantA);
        await using var scope = arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        var handler = new ObtenerDetalleVisitaQueryHandler(contexto, contexto, contexto, contexto, Alcance(contexto));
        return await handler.Handle(new ObtenerDetalleVisitaQuery(visitaId), CancellationToken.None);
    }

    private AlcanceDatosService Alcance(CaeManagerDbContext contexto) =>
        new(contexto, _usuario, _tenant, new SesionPrivilegiadaAusente());

    private static async Task<List<(Guid DocumentoId, Guid? ActorRealUsuarioId, Guid TenantId)>> LeerRegistrosSensiblesAsync(
        ArnesDeArranqueRuntime arnes)
    {
        await using var conexion = new NpgsqlConnection(arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var orden = new NpgsqlCommand(
            """SELECT "DocumentoId", "ActorRealUsuarioId", "TenantId" FROM "RegistrosAccesoDocumentoSensible" """, conexion);
        await using var lector = await orden.ExecuteReaderAsync();

        var filas = new List<(Guid, Guid?, Guid)>();
        while (await lector.ReadAsync())
            filas.Add((lector.GetGuid(0), await lector.IsDBNullAsync(1) ? null : lector.GetGuid(1), lector.GetGuid(2)));
        return filas;
    }

    private static List<string> LeerZip(byte[] contenido)
    {
        using var zip = new ZipArchive(new MemoryStream(contenido), ZipArchiveMode.Read);
        return zip.Entries.Select(e =>
        {
            using var lector = new StreamReader(e.Open());
            return lector.ReadToEnd();
        }).ToList();
    }

    private Task<string> GuardarAsync(string contenido) =>
        _almacenamiento.GuardarAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(contenido)), "doc.pdf");

    /// <summary>
    /// Usuario de la petición y, a la vez, su autoría: la Gestora CAE sin Sesión
    /// Privilegiada, así que Actor real y usuario coinciden.
    /// </summary>
    private sealed class UsuarioConmutable : ICurrentUserService, IActorAuditoria
    {
        public Guid? UsuarioId { get; set; }
        public string? Rol { get; set; }
        public Guid? TenantOrigenId { get; set; }

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(UsuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(Rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(Rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(TenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);

        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(UsuarioId!.Value));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(UsuarioId!.Value);
    }

    private sealed class AlmacenamientoEnMemoria : IFileStorageService
    {
        private readonly Dictionary<string, byte[]> _archivos = [];

        public async Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default)
        {
            using var memoria = new MemoryStream();
            await contenido.CopyToAsync(memoria, cancellationToken);
            var identificador = $"documentos/{Guid.NewGuid():N}{Path.GetExtension(nombreArchivoOriginal)}";
            _archivos[identificador] = memoria.ToArray();
            return identificador;
        }

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(new MemoryStream(_archivos[identificador]));

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default)
        {
            _archivos.Remove(identificador);
            return Task.CompletedTask;
        }
    }
}
