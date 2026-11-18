using CaeManager.Application.Common;
using CaeManager.Application.Retencion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Retencion;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using System.Data.Common;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Retencion;

/// <summary>
/// La ejecución ya autorizada — el único punto que destruye datos
/// personales. P0-3 de Project-Hydra-Negocio/MATURITY_REVIEW.md: comprueba que
/// alcanza también a las filas soft-deleted (EstaEliminado), que antes del
/// fix quedaban invisibles tanto para detectar como para anonimizar.
/// </summary>
public class EjecucionPurgaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = new(2031, 6, 1);
    private readonly Guid _usuarioAutorizador = Guid.NewGuid();

    private Guid _clienteId;
    private Guid _tipoDocumentoId;
    private Guid _centroId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Purgable Ejecucion S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        var empresa = new Empresa("Contrata Purgable Ejecucion S.L.", "B87654323");
        contexto.Empresas.Add(empresa);

        var tipo = new TipoDocumento("Seguro RC Ejecucion", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Purgable Ejecucion", "Calle Falsa 2");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _tipoDocumentoId = tipo.Id;
        _centroId = centro.Id;
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Ejecutar_anonimiza_un_trabajador_dado_de_baja_y_eliminado_logicamente()
    {
        Guid trabajadorId;
        await using (var contextoSiembra = CrearContexto())
        {
            var empresaId = await contextoSiembra.Empresas.Select(e => e.Id).FirstAsync();
            var trabajador = Trabajador.DeEmpresa(
                empresaId, "Manuel", "Moreno", "AB1234567", new DateOnly(1985, 1, 1), null, null, null);
            contextoSiembra.Trabajadores.Add(trabajador);
            await contextoSiembra.SaveChangesAsync();

            var fechaBaja = _hoy.AddYears(-6);
            var asignacion = new Asignacion(trabajador.Id, _centroId, fechaBaja.AddYears(-1));
            asignacion.DarDeBaja(fechaBaja);
            contextoSiembra.Asignaciones.Add(asignacion);

            // Su asignación a una Visita guarda la foto (documento y nombre) para la exportación de extranjeros del mes.
            var visita = new Visita(_centroId, fechaBaja.AddMonths(-3), fechaBaja.AddMonths(-3), null);
            contextoSiembra.Visitas.Add(visita);
            await contextoSiembra.SaveChangesAsync();
            contextoSiembra.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajador));
            await contextoSiembra.SaveChangesAsync();

            // El caso central del hallazgo: además de dado de baja, eliminado lógicamente.
            trabajador.MarcarComoEliminado(Guid.NewGuid());
            await contextoSiembra.SaveChangesAsync();

            trabajadorId = trabajador.Id;
        }

        Guid solicitudId;
        await using (var contextoDeteccion = CrearContexto())
        {
            var servicioDeteccion = new DeteccionPurgaService(
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                new SolicitudPurgaRepository(contextoDeteccion),
                Microsoft.Extensions.Options.Options.Create(new RetencionDatosOptions
                {
                    AniosRetencionTrabajadores = 5
                }),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoDeteccion);

            (await servicioDeteccion.DetectarAsync(_hoy)).Should().Be(1);

            var solicitud = await contextoDeteccion.SolicitudesPurga
                .SingleAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja);
            solicitudId = solicitud.Id;
        }

        await using (var contextoAutorizacion = CrearContexto())
        {
            var solicitud = await contextoAutorizacion.SolicitudesPurga.FirstAsync(s => s.Id == solicitudId);
            solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contextoAutorizacion.SaveChangesAsync();
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion,
                contextoEjecucion,
                contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new ExtraccionIaCacheRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                new TransaccionDeComando(contextoEjecucion),
                new EnmascaradoAuditoriaTrabajadorAnonimizado(contextoEjecucion),
                new AlertaOperativaFalsa(),
                NullLogger<EjecucionPurgaService>.Instance);

            var afectados = await servicioEjecucion.EjecutarAsync(solicitudId, _hoy);
            afectados.Should().Be(1);
        }

        await using var contextoVerificacion = CrearContexto();
        var trabajadorAnonimizado = await contextoVerificacion.Trabajadores
            .IgnoreQueryFilters()
            .FirstAsync(t => t.Id == trabajadorId);

        trabajadorAnonimizado.EstaAnonimizado.Should().BeTrue(
            "el trabajador estaba soft-deleted pero seguía dentro del plazo de retención — debía anonimizarse igual");

        var foto = await contextoVerificacion.VisitasTrabajadores.SingleAsync(vt => vt.TrabajadorId == trabajadorId);
        foto.DocumentoIdentidad.Should().BeNull("la foto de la asignación copia un dato personal que se suprime con el Trabajador");
        foto.NombreMostrado.Should().BeNull("la foto de la asignación copia un dato personal que se suprime con el Trabajador");
        foto.EsExtranjeroCobrable.Should().BeTrue("la asignación ya se gestionó y se cobró: la marca no es dato personal");

        var solicitudEjecutada = await contextoVerificacion.SolicitudesPurga.FirstAsync(s => s.Id == solicitudId);
        solicitudEjecutada.ResultadoEjecucion.Should().Be(ResultadoEjecucionPurga.Completa,
            "anonimizar un Trabajador no tiene paso de almacenamiento externo que pueda fallar a medias");
    }

    /// <summary>
    /// Anonimizar y enmascarar el historial de auditoría van en la misma transacción
    /// (decisión de Chris, 2026-10-15): si la función de enmascarado falla —aquí, sin
    /// Tenant en la sesión, 42501—, la anonimización del Trabajador y el paso de la
    /// solicitud a Ejecutada tampoco se confirman.
    /// </summary>
    [Fact]
    public async Task Si_falla_el_enmascarado_del_historial_la_anonimizacion_no_se_confirma()
    {
        Guid trabajadorId;
        await using (var contextoSiembra = CrearContexto())
        {
            var empresaId = await contextoSiembra.Empresas.Select(e => e.Id).FirstAsync();
            var trabajador = Trabajador.DeEmpresa(
                empresaId, "Manuel", "Moreno", "12345678Z", new DateOnly(1985, 1, 1), null, null, null);
            contextoSiembra.Trabajadores.Add(trabajador);
            await contextoSiembra.SaveChangesAsync();

            var fechaBaja = _hoy.AddYears(-6);
            var asignacion = new Asignacion(trabajador.Id, _centroId, fechaBaja.AddYears(-1));
            asignacion.DarDeBaja(fechaBaja);
            contextoSiembra.Asignaciones.Add(asignacion);
            await contextoSiembra.SaveChangesAsync();
            trabajadorId = trabajador.Id;
        }

        Guid solicitudId;
        await using (var contextoDeteccion = CrearContexto())
        {
            var servicioDeteccion = new DeteccionPurgaService(
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                new SolicitudPurgaRepository(contextoDeteccion),
                Microsoft.Extensions.Options.Options.Create(new RetencionDatosOptions
                {
                    AniosRetencionTrabajadores = 5
                }),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoDeteccion);

            (await servicioDeteccion.DetectarAsync(_hoy)).Should().Be(1);
            var solicitud = await contextoDeteccion.SolicitudesPurga
                .SingleAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja);
            solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contextoDeteccion.SaveChangesAsync();
            solicitudId = solicitud.Id;
        }

        await using (var contextoEjecucion = CrearContexto(tenantEnLaSesion: false))
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion,
                contextoEjecucion,
                contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new ExtraccionIaCacheRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                new TransaccionDeComando(contextoEjecucion),
                new EnmascaradoAuditoriaTrabajadorAnonimizado(contextoEjecucion),
                new AlertaOperativaFalsa(),
                NullLogger<EjecucionPurgaService>.Instance);

            var ejecutar = () => servicioEjecucion.EjecutarAsync(solicitudId, _hoy);
            await ejecutar.Should().ThrowAsync<Npgsql.PostgresException>()
                .Where(e => e.SqlState == "42501");
        }

        await using var contextoVerificacion = CrearContexto();
        var trabajadorSinTocar = await contextoVerificacion.Trabajadores
            .IgnoreQueryFilters()
            .FirstAsync(t => t.Id == trabajadorId);
        trabajadorSinTocar.EstaAnonimizado.Should().BeFalse(
            "el enmascarado del historial falló y debía arrastrar consigo a la anonimización");
        trabajadorSinTocar.Nombre.Should().Be("Manuel");

        var solicitudSinEjecutar = await contextoVerificacion.SolicitudesPurga.FirstAsync(s => s.Id == solicitudId);
        solicitudSinEjecutar.Estado.Should().Be(EstadoSolicitudPurga.Programada);
        solicitudSinEjecutar.ResultadoEjecucion.Should().BeNull();
    }

    /// <summary>
    /// Auditoría Módulo 5, hallazgo crítico 9/9: Trabajador.Anonimizar vaciaba
    /// el Dni a '' en vez de a null, y el índice único (TenantId, Dni) no
    /// tenía filtro. Con dos trabajadores del mismo tenant purgables en la
    /// MISMA ejecución, el SaveChangesAsync único de EjecucionPurgaService
    /// (todo el lote en una transacción) chocaba con un 23505 al llegar al
    /// segundo — la purga entera fallaba, no solo el segundo trabajador.
    /// </summary>
    [Fact]
    public async Task Ejecutar_anonimiza_dos_trabajadores_purgables_del_mismo_tenant_en_la_misma_ejecucion()
    {
        var fechaBaja = _hoy.AddYears(-6);
        Guid primeroId, segundoId;

        await using (var contextoSiembra = CrearContexto())
        {
            var empresaId = await contextoSiembra.Empresas.Select(e => e.Id).FirstAsync();

            var primero = Trabajador.DeEmpresa(
                empresaId, "Manuel", "Moreno", "12345678Z", new DateOnly(1985, 1, 1), null, null, null);
            var segundo = Trabajador.DeEmpresa(
                empresaId, "Pedro", "Gómez", "87654321X", new DateOnly(1988, 3, 20), null, null, null);
            contextoSiembra.Trabajadores.AddRange(primero, segundo);
            await contextoSiembra.SaveChangesAsync();

            var asignacionPrimero = new Asignacion(primero.Id, _centroId, fechaBaja.AddYears(-1));
            asignacionPrimero.DarDeBaja(fechaBaja);
            var asignacionSegundo = new Asignacion(segundo.Id, _centroId, fechaBaja.AddYears(-1));
            asignacionSegundo.DarDeBaja(fechaBaja);
            contextoSiembra.Asignaciones.AddRange(asignacionPrimero, asignacionSegundo);
            await contextoSiembra.SaveChangesAsync();

            primeroId = primero.Id;
            segundoId = segundo.Id;
        }

        Guid solicitudId;
        await using (var contextoDeteccion = CrearContexto())
        {
            var servicioDeteccion = new DeteccionPurgaService(
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                new SolicitudPurgaRepository(contextoDeteccion),
                Microsoft.Extensions.Options.Options.Create(new RetencionDatosOptions
                {
                    AniosRetencionTrabajadores = 5
                }),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoDeteccion);

            await servicioDeteccion.DetectarAsync(_hoy);

            var solicitud = await contextoDeteccion.SolicitudesPurga
                .SingleAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja);
            solicitud.RegistrosAfectados.Should().Be(2, "los dos trabajadores cumplen el mismo plazo");
            solicitudId = solicitud.Id;
        }

        await using (var contextoAutorizacion = CrearContexto())
        {
            var solicitud = await contextoAutorizacion.SolicitudesPurga.FirstAsync(s => s.Id == solicitudId);
            solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contextoAutorizacion.SaveChangesAsync();
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion,
                contextoEjecucion,
                contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new ExtraccionIaCacheRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                new TransaccionDeComando(contextoEjecucion),
                new EnmascaradoAuditoriaTrabajadorAnonimizado(contextoEjecucion),
                new AlertaOperativaFalsa(),
                NullLogger<EjecucionPurgaService>.Instance);

            // Antes de la corrección, esto lanzaba DbUpdateException (23505)
            // al llegar al segundo trabajador y tumbaba la purga entera.
            var afectados = await servicioEjecucion.EjecutarAsync(solicitudId, _hoy);
            afectados.Should().Be(2);
        }

        await using var contextoVerificacion = CrearContexto();
        var primero_ = await contextoVerificacion.Trabajadores.IgnoreQueryFilters().SingleAsync(t => t.Id == primeroId);
        var segundo_ = await contextoVerificacion.Trabajadores.IgnoreQueryFilters().SingleAsync(t => t.Id == segundoId);

        primero_.EstaAnonimizado.Should().BeTrue();
        primero_.Dni.Should().BeNull();
        segundo_.EstaAnonimizado.Should().BeTrue();
        segundo_.Dni.Should().BeNull();
    }

    /// <summary>
    /// Siembra un Documento de Cliente ya vencido y con archivo adjunto, más
    /// la solicitud de purga que lo alcanza, ya autorizada. Devuelve los dos
    /// identificadores que los casos necesitan comprobar.
    /// </summary>
    private async Task<(Guid DocumentoId, Guid SolicitudId)> SembrarDocumentoPurgableAsync(string archivoUrl)
    {
        await using var contexto = CrearContexto();

        var documento = Documento.DeCliente(
            _clienteId, _tipoDocumentoId,
            fechaEmision: _hoy.AddYears(-7),
            vigencia: VigenciaDocumento.VenceEl(_hoy.AddYears(-6)),
            archivoUrl: archivoUrl);
        contexto.Documentos.Add(documento);

        // R4: la solicitud de Documentos purga solo la lista que se propuso al detectarla.
        var (solicitud, propuestos) = SolicitudPurga.DeDocumentos([documento.Id], _hoy.AddYears(-5));
        contexto.SolicitudesPurga.Add(solicitud);
        contexto.DocumentosPropuestosPurga.AddRange(propuestos);
        await contexto.SaveChangesAsync();

        solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
        await contexto.SaveChangesAsync();

        return (documento.Id, solicitud.Id);
    }

    [Fact]
    public async Task Si_no_se_puede_borrar_el_archivo_el_documento_no_se_da_por_purgado()
    {
        // El orden anterior anonimizaba primero y borraba después: al fallar el
        // borrado, la fila quedaba marcada como anonimizada y sin ArchivoUrl,
        // el PDF seguía en almacenamiento y ya nada sabía qué había que borrar.
        // Un reconocimiento médico conservado para siempre y declarado
        // suprimido. Este caso fija que un borrado fallido deja el documento
        // intacto y localizable, para que un reintento posterior lo alcance.
        var (documentoId, solicitudId) = await SembrarDocumentoPurgableAsync("tenant/medico.pdf");

        var alertas = new AlertaOperativaFalsa();
        await using (var contextoEjecucion = CrearContexto())
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new ExtraccionIaCacheRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(fallaAlEliminar: true),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                new TransaccionDeComando(contextoEjecucion),
                new EnmascaradoAuditoriaTrabajadorAnonimizado(contextoEjecucion),
                alertas,
                NullLogger<EjecucionPurgaService>.Instance);

            var afectados = await servicioEjecucion.EjecutarAsync(solicitudId, _hoy);
            afectados.Should().Be(0, "no se purgó nada: el archivo sigue estando");
        }

        await using var contextoVerificacion = CrearContexto();
        var documento = await contextoVerificacion.Documentos
            .IgnoreQueryFilters()
            .FirstAsync(d => d.Id == documentoId);

        documento.EstaAnonimizado.Should().BeFalse(
            "declararlo purgado con su PDF todavía en almacenamiento sería conformidad falsa");
        documento.ArchivoUrl.Should().Be("tenant/medico.pdf",
            "sin la referencia, ningún reintento posterior podría encontrar el archivo superviviente");

        alertas.Alertas.Should().ContainSingle()
            .Which.Nivel.Should().Be(NivelAlertaOperativa.Critica,
                "la solicitud ya quedó marcada como ejecutada, así que nadie reintentará esto solo");

        // Opción 4 (decisión de producto 2026-09-11): "Ejecutada" no equivale
        // a "completa". El resultado durable de la ejecución debe distinguir
        // este caso, con trazabilidad mínima del candidato que falló.
        var solicitud = await contextoVerificacion.SolicitudesPurga.FirstAsync(s => s.Id == solicitudId);
        solicitud.ResultadoEjecucion.Should().Be(ResultadoEjecucionPurga.ConIncidencias);
        solicitud.CandidatosEnEjecucion.Should().Be(1);
        solicitud.SuprimidosEnEjecucion.Should().Be(0);
        solicitud.FallidosEnEjecucion.Should().Be(1);

        var incidencia = await contextoVerificacion.IncidenciasPurga.SingleAsync(i => i.SolicitudPurgaId == solicitudId);
        incidencia.ObjetivoId.Should().Be(documentoId);
        incidencia.Tipo.Should().Be(TipoIncidenciaPurga.FalloEliminacionArchivo);
    }

    [Fact]
    public async Task Un_documento_vencido_se_anonimiza_y_pierde_su_archivo()
    {
        // Control positivo del caso anterior: sin él, un servicio que no
        // purgara nunca nada también pasaría aquel.
        var (documentoId, solicitudId) = await SembrarDocumentoPurgableAsync("tenant/medico.pdf");

        var alertas = new AlertaOperativaFalsa();
        await using (var contextoEjecucion = CrearContexto())
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new ExtraccionIaCacheRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                new TransaccionDeComando(contextoEjecucion),
                new EnmascaradoAuditoriaTrabajadorAnonimizado(contextoEjecucion),
                alertas,
                NullLogger<EjecucionPurgaService>.Instance);

            (await servicioEjecucion.EjecutarAsync(solicitudId, _hoy)).Should().Be(1);
        }

        await using var contextoVerificacion = CrearContexto();
        var documento = await contextoVerificacion.Documentos
            .IgnoreQueryFilters()
            .FirstAsync(d => d.Id == documentoId);

        documento.EstaAnonimizado.Should().BeTrue();
        documento.ArchivoUrl.Should().BeNull();
        alertas.Alertas.Should().BeEmpty();

        var solicitud = await contextoVerificacion.SolicitudesPurga.FirstAsync(s => s.Id == solicitudId);
        solicitud.ResultadoEjecucion.Should().Be(ResultadoEjecucionPurga.Completa);
        solicitud.CandidatosEnEjecucion.Should().Be(1);
        solicitud.SuprimidosEnEjecucion.Should().Be(1);
        solicitud.FallidosEnEjecucion.Should().Be(0);

        (await contextoVerificacion.IncidenciasPurga.AnyAsync(i => i.SolicitudPurgaId == solicitudId))
            .Should().BeFalse("una ejecución completa no deja incidencias");
    }

    /// <summary>
    /// R4 (decisión del 2026-11-18): la ejecución purga la lista que propuso la detección y nada más. Un Documento que
    /// también ha cumplido su plazo, pero no estaba en la propuesta (subido después, o alcanzado al acortar un plazo),
    /// espera a una propuesta nueva: una solicitud abierta nunca se amplía.
    /// </summary>
    [Fact]
    public async Task La_ejecucion_no_purga_un_Documento_que_no_estaba_en_la_propuesta()
    {
        var (propuestoId, solicitudId) = await SembrarDocumentoPurgableAsync("tenant/propuesto.pdf");

        Guid fueraId;
        await using (var contexto = CrearContexto())
        {
            var fuera = Documento.DeCliente(
                _clienteId, _tipoDocumentoId,
                fechaEmision: _hoy.AddYears(-9),
                vigencia: VigenciaDocumento.VenceEl(_hoy.AddYears(-8)),
                archivoUrl: "tenant/fuera.pdf");
            contexto.Documentos.Add(fuera);
            await contexto.SaveChangesAsync();
            fueraId = fuera.Id;
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion, contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new ExtraccionIaCacheRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                new TransaccionDeComando(contextoEjecucion),
                new EnmascaradoAuditoriaTrabajadorAnonimizado(contextoEjecucion),
                new AlertaOperativaFalsa(),
                NullLogger<EjecucionPurgaService>.Instance);

            (await servicioEjecucion.EjecutarAsync(solicitudId, _hoy)).Should().Be(1);
        }

        await using var contextoVerificacion = CrearContexto();
        (await contextoVerificacion.Documentos.IgnoreQueryFilters().SingleAsync(d => d.Id == propuestoId))
            .EstaAnonimizado.Should().BeTrue();
        var noPropuesto = await contextoVerificacion.Documentos.IgnoreQueryFilters().SingleAsync(d => d.Id == fueraId);
        noPropuesto.EstaAnonimizado.Should().BeFalse("no estaba en la lista de la solicitud");
        noPropuesto.ArchivoUrl.Should().Be("tenant/fuera.pdf");
    }

    /// <summary>
    /// La lista propuesta es el tope, no la orden: la ejecución vuelve a aplicar el criterio con las fechas de hoy.
    /// Si entre la detección y la ejecución se corrige la vigencia del Documento y ya no ha cumplido su plazo, se
    /// conserva. El desvío va siempre hacia conservar (R4).
    /// </summary>
    [Fact]
    public async Task Un_Documento_propuesto_cuya_vigencia_se_corrige_antes_de_ejecutar_se_conserva()
    {
        var documentoId = await SembrarDocumentoAsync(
            _hoy.AddYears(-7), _hoy.AddYears(-6), "tenant/corregido.pdf");
        var solicitudId = await DetectarYProgramarDocumentosAsync([documentoId]);

        await using (var contexto = CrearContexto())
        {
            var documento = await contexto.Documentos.SingleAsync(d => d.Id == documentoId);
            documento.CorregirVigencia(documento.FechaEmision, VigenciaDocumento.VenceEl(_hoy.AddYears(-1)));
            await contexto.SaveChangesAsync();
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            (await CrearEjecucion(contextoEjecucion).EjecutarAsync(solicitudId, _hoy))
                .Should().Be(0, "con la vigencia corregida el Documento aún no ha cumplido su plazo");
        }

        await using var contextoVerificacion = CrearContexto();
        var conservado = await contextoVerificacion.Documentos.IgnoreQueryFilters().SingleAsync(d => d.Id == documentoId);
        conservado.EstaAnonimizado.Should().BeFalse();
        conservado.ArchivoUrl.Should().Be("tenant/corregido.pdf");
    }

    /// <summary>
    /// Alargar el plazo del Tenant propietario después de la detección protege lo propuesto que, con el plazo nuevo,
    /// todavía no lo ha cumplido: la ejecución lee el plazo de hoy, no el de la detección.
    /// </summary>
    [Fact]
    public async Task Alargar_el_plazo_del_Tenant_despues_de_la_deteccion_conserva_lo_propuesto()
    {
        await using (var contexto = CrearContexto())
        {
            var parametros = new ParametroSistema(30, 15);
            parametros.FijarPlazoRetencionDocumentos(1);
            contexto.ParametrosSistema.Add(parametros);
            await contexto.SaveChangesAsync();
        }

        // Vencido hace dos años: con 1 año de plazo se propone; con 5, no.
        var documentoId = await SembrarDocumentoAsync(
            _hoy.AddYears(-7), _hoy.AddYears(-2), "tenant/alargado.pdf");
        var solicitudId = await DetectarYProgramarDocumentosAsync([documentoId]);

        await using (var contexto = CrearContexto())
        {
            var parametros = await contexto.ParametrosSistema.SingleAsync();
            parametros.FijarPlazoRetencionDocumentos(5);
            await contexto.SaveChangesAsync();
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            (await CrearEjecucion(contextoEjecucion).EjecutarAsync(solicitudId, _hoy))
                .Should().Be(0, "con el plazo alargado a 5 años el Documento aún no lo ha cumplido");
        }

        await using var contextoVerificacion = CrearContexto();
        var conservado = await contextoVerificacion.Documentos.IgnoreQueryFilters().SingleAsync(d => d.Id == documentoId);
        conservado.EstaAnonimizado.Should().BeFalse();
        conservado.ArchivoUrl.Should().Be("tenant/alargado.pdf");
    }

    /// <summary>
    /// La lista de una solicitud cancelada no cuenta para otra del mismo Tenant propietario: la ejecución purga solo
    /// lo propuesto en la solicitud que ejecuta, aunque lo de la cancelada también haya cumplido su plazo.
    /// </summary>
    [Fact]
    public async Task La_lista_de_una_solicitud_cancelada_no_se_purga_al_ejecutar_otra_del_mismo_Tenant()
    {
        var deLaCanceladaId = await SembrarDocumentoAsync(
            _hoy.AddYears(-8), _hoy.AddYears(-7), "tenant/cancelada.pdf");
        var deLaProgramadaId = await SembrarDocumentoAsync(
            _hoy.AddYears(-8), _hoy.AddYears(-7), "tenant/programada.pdf");

        Guid canceladaId, programadaId;
        await using (var contexto = CrearContexto())
        {
            var (cancelada, propuestosCancelada) = SolicitudPurga.DeDocumentos([deLaCanceladaId], _hoy.AddYears(-5));
            contexto.SolicitudesPurga.Add(cancelada);
            contexto.DocumentosPropuestosPurga.AddRange(propuestosCancelada);
            await contexto.SaveChangesAsync();
            cancelada.Cancelar("El Tenant propietario pidió conservarlo.");
            await contexto.SaveChangesAsync();

            var (programada, propuestosProgramada) = SolicitudPurga.DeDocumentos([deLaProgramadaId], _hoy.AddYears(-5));
            contexto.SolicitudesPurga.Add(programada);
            contexto.DocumentosPropuestosPurga.AddRange(propuestosProgramada);
            await contexto.SaveChangesAsync();
            programada.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contexto.SaveChangesAsync();

            canceladaId = cancelada.Id;
            programadaId = programada.Id;
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            (await CrearEjecucion(contextoEjecucion).EjecutarAsync(programadaId, _hoy)).Should().Be(1);
        }

        await using var contextoVerificacion = CrearContexto();
        (await contextoVerificacion.Documentos.IgnoreQueryFilters().SingleAsync(d => d.Id == deLaProgramadaId))
            .EstaAnonimizado.Should().BeTrue();
        var deLaCancelada = await contextoVerificacion.Documentos.IgnoreQueryFilters().SingleAsync(d => d.Id == deLaCanceladaId);
        deLaCancelada.EstaAnonimizado.Should().BeFalse("solo estaba en la lista de la solicitud cancelada");
        deLaCancelada.ArchivoUrl.Should().Be("tenant/cancelada.pdf");
        (await contextoVerificacion.SolicitudesPurga.SingleAsync(s => s.Id == canceladaId))
            .Estado.Should().Be(EstadoSolicitudPurga.Cancelada);
    }

    /// <summary>
    /// Una solicitud de Documentos anterior al plazo por Tenant propietario no guarda la lista de lo propuesto. Falla
    /// cerrada: no purga nada, no se cierra como ejecutada y lo dice con un error explícito, que el comando devuelve
    /// como <c>SolicitudPurga.NoEjecutable</c>.
    /// </summary>
    [Fact]
    public async Task Una_solicitud_de_Documentos_sin_lista_de_propuestos_no_se_ejecuta()
    {
        var documentoId = await SembrarDocumentoAsync(
            _hoy.AddYears(-8), _hoy.AddYears(-7), "tenant/sin-lista.pdf");

        Guid solicitudId;
        await using (var contexto = CrearContexto())
        {
            var solicitud = new SolicitudPurga(TipoDatoPurgable.Documentos, 1, _hoy.AddYears(-5));
            contexto.SolicitudesPurga.Add(solicitud);
            await contexto.SaveChangesAsync();
            solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contexto.SaveChangesAsync();
            solicitudId = solicitud.Id;
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            var ejecutar = () => CrearEjecucion(contextoEjecucion).EjecutarAsync(solicitudId, _hoy);
            (await ejecutar.Should().ThrowAsync<InvalidOperationException>())
                .Which.Message.Should().Be(EjecucionPurgaService.SinListaDePropuestos);
        }

        await using var contextoVerificacion = CrearContexto();
        var sinEjecutar = await contextoVerificacion.SolicitudesPurga.SingleAsync(s => s.Id == solicitudId);
        sinEjecutar.Estado.Should().Be(EstadoSolicitudPurga.Programada);
        sinEjecutar.ResultadoEjecucion.Should().BeNull();
        var documento = await contextoVerificacion.Documentos.IgnoreQueryFilters().SingleAsync(d => d.Id == documentoId);
        documento.EstaAnonimizado.Should().BeFalse();
        documento.ArchivoUrl.Should().Be("tenant/sin-lista.pdf");
    }

    private async Task<Guid> SembrarDocumentoAsync(DateOnly fechaEmision, DateOnly vencimiento, string archivoUrl)
    {
        await using var contexto = CrearContexto();
        var documento = Documento.DeCliente(
            _clienteId, _tipoDocumentoId,
            fechaEmision: fechaEmision,
            vigencia: VigenciaDocumento.VenceEl(vencimiento),
            archivoUrl: archivoUrl);
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();
        return documento.Id;
    }

    /// <summary>
    /// Pasa por la detección real, comprueba que propuso justo <paramref name="esperados"/> y programa la solicitud
    /// para hoy, como haría quien la autoriza.
    /// </summary>
    private async Task<Guid> DetectarYProgramarDocumentosAsync(IReadOnlyCollection<Guid> esperados)
    {
        await using var contexto = CrearContexto();
        var deteccion = new DeteccionPurgaService(
            contexto, contexto, contexto, contexto, contexto,
            new SolicitudPurgaRepository(contexto),
            Microsoft.Extensions.Options.Options.Create(new RetencionDatosOptions()),
            new TenantActualAmbiental { TenantId = _tenant },
            contexto);
        await deteccion.DetectarAsync(_hoy);

        var solicitud = await contexto.SolicitudesPurga.SingleAsync(s => s.TipoDato == TipoDatoPurgable.Documentos);
        (await contexto.DocumentosPropuestosPurga
                .Where(p => p.SolicitudPurgaId == solicitud.Id)
                .Select(p => p.DocumentoId)
                .ToListAsync())
            .Should().BeEquivalentTo(esperados);

        solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
        await contexto.SaveChangesAsync();
        return solicitud.Id;
    }

    private EjecucionPurgaService CrearEjecucion(CaeManagerDbContext contexto) => new(
        contexto, contexto, contexto, contexto, contexto, contexto, contexto,
        new SolicitudPurgaRepository(contexto),
        new ExtraccionIaCacheRepository(contexto),
        new AlmacenamientoArchivosFalso(),
        new TenantActualAmbiental { TenantId = _tenant },
        contexto,
        new TransaccionDeComando(contexto),
        new EnmascaradoAuditoriaTrabajadorAnonimizado(contexto),
        new AlertaOperativaFalsa(),
        NullLogger<EjecucionPurgaService>.Instance);

    private CaeManagerDbContext CrearContexto(bool tenantEnLaSesion = true)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var builder = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual));
        if (tenantEnLaSesion) builder.AddInterceptors(new TenantEnLaSesion(_tenant));
        var options = builder.Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// El Tenant del contexto RLS firmado en cada conexión, como hace <c>TenantRlsConnectionInterceptor</c>
    /// en producción: la ejecución de una purga de Trabajadores llama a
    /// <c>app_enmascarar_auditoria_de_trabajador_anonimizado</c>, que sin Tenant en la
    /// sesión falla con 42501. Estos tests conectan como propietario (sin RLS), así que
    /// fijarlo no cambia lo que ven.
    /// </summary>
    private sealed class TenantEnLaSesion(Guid tenant) : DbConnectionInterceptor
    {
        public override async Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            await ContextoRlsDePruebas.FijarAsync((NpgsqlConnection)connection, tenantId: tenant);
        }
    }

    private sealed class AlmacenamientoArchivosFalso(bool fallaAlEliminar = false) : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            fallaAlEliminar
                ? throw new IOException("El almacenamiento no está disponible.")
                : Task.CompletedTask;
    }

    private sealed class AlertaOperativaFalsa : IAlertaOperativa
    {
        public List<(string Mensaje, NivelAlertaOperativa Nivel)> Alertas { get; } = [];

        public void Emitir(string mensaje, NivelAlertaOperativa nivel) => Alertas.Add((mensaje, nivel));

        public void CapturarExcepcion(Exception excepcion) { }

        public void DejarMigaDePan(string mensaje) { }

        public IDisposable IniciarAmbitoDeCaptura() => new AmbitoVacio();

        private sealed class AmbitoVacio : IDisposable
        {
            public void Dispose() { }
        }
    }
}
