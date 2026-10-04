using CaeManager.Domain.Common;
using CaeManager.Application.Centros;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// Pieza 5 del backlog de gestión EPI: CalculadoraEstadoCentro (Domain) ya
/// tiene sus propios tests puros — estos verifican que
/// CalculoEstadoCentroService reúne correctamente los datos reales
/// (Documentos de Empresa/Trabajador, huecos obligatorios,
/// TipoDocumentoCentro.BloqueaAcceso) contra Postgres, incluyendo los joins
/// que un test en memoria no ejercita.
/// </summary>
public class CalculoEstadoCentroServiceTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centroId;
    private Guid _empresaId;
    private Guid _trabajadorId;
    private Guid _tipoDocumentoObligatorioId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente EstadoCentro S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa EstadoCentro S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro EstadoCentro");
        contexto.Centros.Add(centro);

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "77189989B");
        contexto.Trabajadores.Add(trabajador);

        var tipoObligatorio = new TipoDocumento("EPIs", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipoObligatorio);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DiaDeNegocio.Hoy()));
        await contexto.SaveChangesAsync();

        _centroId = centro.Id;
        _empresaId = empresa.Id;
        _trabajadorId = trabajador.Id;
        _tipoDocumentoObligatorioId = tipoObligatorio.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // --- Vigencia en la plataforma ---------------------------------------
    //
    // Decisión del propietario (2026-09-21): un documento vencido EN LA
    // PLATAFORMA sale en el semáforo del Centro aunque en TALVEG siga vigente.
    // Desde 2026-10-04 (D-7) no lo pone en «Bloqueado»: el bloqueo es del
    // Trabajador o de la Empresa afectados y lo dice el evaluador de acceso
    // (LaPlataformaBloqueaAlTrabajadorYNoAlCentroTests); aquí el Centro queda «Vencido».

    [Fact]
    public async Task Vencido_en_la_plataforma_deja_el_Centro_Vencido_y_no_Bloqueado_aunque_el_documento_siga_vigente_en_TALVEG()
    {
        await SembrarAcreditacionAsync(
            VigenciaEnPlataforma.VenceEl(DiaDeNegocio.Hoy().AddDays(-1)));

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vencido);
        resultado.Causas.Should().Contain(c =>
            c.Estado == EstadoDocumento.Vencido && c.Descripcion.Contains("vencido en la plataforma"));
    }

    [Fact]
    public async Task Sin_confirmar_la_vigencia_NO_marca_el_Centro()
    {
        // Mira en dirección contraria a la prueba de arriba, y es la que de
        // verdad protege: al desplegar esto, TODAS las acreditaciones que ya
        // existen quedan sin confirmar. Si "no lo sé" contara como vencido, el
        // día del despliegue todos los Centros se pondrían en rojo a la vez.
        await SembrarAcreditacionAsync(VigenciaEnPlataforma.SinConfirmar);

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().NotContain(c => c.Descripcion.Contains("plataforma"));
    }

    [Fact]
    public async Task Vigente_en_la_plataforma_no_anade_ninguna_causa()
    {
        // Control positivo del sembrado: sin esto, las dos pruebas de arriba
        // pasarían igual si SembrarAcreditacionAsync no escribiera nada.
        await SembrarAcreditacionAsync(
            VigenciaEnPlataforma.VenceEl(DiaDeNegocio.Hoy().AddMonths(6)));

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().BeEmpty();
    }

    [Fact]
    public async Task Un_trabajador_desvinculado_no_marca_el_Centro_por_su_vigencia_en_la_plataforma()
    {
        // Misma acreditación vencida que la primera prueba de este bloque: la
        // única diferencia es la baja de la Asignación. La acreditación no
        // muere con ella —sigue en la base de datos, vencida—, así que sin el
        // filtro por asignación activa un Trabajador que ya no pisa el Centro
        // lo marcaría indefinidamente.
        await SembrarAcreditacionAsync(
            VigenciaEnPlataforma.VenceEl(DiaDeNegocio.Hoy().AddDays(-1)));

        await using (var contexto = CrearContexto())
        {
            var asignacion = await contexto.Asignaciones.SingleAsync(a => a.TrabajadorId == _trabajadorId);
            asignacion.DarDeBaja(DiaDeNegocio.Hoy());
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Causas.Should().NotContain(c => c.Descripcion.Contains("vencido en la plataforma"));
    }

    // --- Acreditación rechazada por la plataforma (D-7, piloto Outbound) --------
    //
    // Desde 2026-10-04 una acreditación Rechazada NO es causa del Centro: no describe un
    // documento vencido ni faltante, es un veredicto sobre un Trabajador o una Empresa. Bloquea a ese
    // Trabajador (o a los de esa Empresa) en ese Centro, y eso es del evaluador de acceso por
    // Trabajador (LaPlataformaBloqueaAlTrabajadorYNoAlCentroTests). Aquí solo se fija
    // que el semáforo del Centro no se entera.

    [Fact]
    public async Task Vigente_en_TALVEG_pero_rechazada_por_la_plataforma_NO_marca_el_Centro_y_si_bloquea_al_Trabajador()
    {
        await SembrarAcreditacionEnAsync(
            _centroId, _trabajadorId, a => a.Rechazar(CausaRechazoAcreditacion.Otro, "Documento ilegible", DateTime.UtcNow));

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().BeEmpty("el rechazo no es una causa del Centro: es un bloqueo del Trabajador");

        // Control positivo: el rechazo existe, es aplicable a este Centro y el evaluador lo ve (si no, lo de arriba no probaría nada).
        await using var contexto = CrearContexto();
        var evaluacion = await new EvaluacionDeAccesoPorCentroService(
                contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso())
            .EvaluarAsync([_centroId], CancellationToken.None);
        evaluacion.Requisitos.Should().ContainSingle(r =>
            r.TrabajadorId == _trabajadorId && r.Resultado.Situacion == SituacionDeRequisitoBloqueante.RechazadoPorPlataforma);
    }

    /// <summary>
    /// Deja el Centro con su documentación al día en TALVEG y una acreditación
    /// de ese documento contra una plataforma, con la vigencia que se le pase.
    /// Así lo único que cambia entre las tres pruebas de arriba es la vigencia
    /// en la plataforma, que es la variable bajo estudio.
    /// </summary>
    private async Task SembrarAcreditacionAsync(VigenciaEnPlataforma vigencia)
    {
        await using var contexto = CrearContexto();

        var documento = Documento.DeTrabajador(
            _trabajadorId, _tipoDocumentoObligatorioId,
            DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddYears(1)));
        contexto.Documentos.Add(documento);

        var proveedor = new ProveedorPlataformaCae("PLAT-PRUEBA", "Plataforma de prueba");
        contexto.ProveedoresPlataformaCae.Add(proveedor);
        await contexto.SaveChangesAsync();

        var canal = CanalGestionDocumental.DePlataforma(
            _centroId, "Acceso de prueba", proveedor.Id, null, null, null);
        contexto.CanalesGestionDocumental.Add(canal);
        await contexto.SaveChangesAsync();

        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        acreditacion.MarcarAceptada(vigencia);
        contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await contexto.SaveChangesAsync();
    }

    [Fact]
    public async Task Retorna_Vigente_cuando_el_unico_trabajador_tiene_su_documento_obligatorio_al_dia()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddYears(1))));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().BeEmpty();
    }

    [Fact]
    public async Task Retorna_Faltante_cuando_el_trabajador_no_tiene_el_documento_obligatorio()
    {
        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Faltante);
        resultado.Causas.Should().ContainSingle(c => c.Estado == EstadoDocumento.Faltante && c.Descripcion.Contains("Ana García"));
    }

    [Fact]
    public async Task Retorna_Vencido_cuando_un_documento_de_la_empresa_esta_vencido()
    {
        Guid tipoEmpresaId;
        Guid documentoEmpresaId;
        var fechaVencimiento = DiaDeNegocio.Hoy().AddDays(-1);

        await using (var contexto = CrearContexto())
        {
            var tipoEmpresa = new TipoDocumento("Seguro RC", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Empresa);
            contexto.TiposDocumento.Add(tipoEmpresa);
            await contexto.SaveChangesAsync();
            tipoEmpresaId = tipoEmpresa.Id;

            var documentoEmpresa = Documento.DeEmpresa(
                _empresaId, tipoEmpresa.Id, DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(fechaVencimiento));
            contexto.Documentos.Add(documentoEmpresa);
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddYears(1))));
            await contexto.SaveChangesAsync();
            documentoEmpresaId = documentoEmpresa.Id;
        }

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vencido);
        // DocumentoId/TipoDocumentoId/FechaVencimiento alimentan la tabla de
        // documentos del bloque Empresa en Centro 360 (Documento · Estado ·
        // Vigencia · Acción) sin una consulta aparte — deben venir poblados.
        resultado.Causas.Should().ContainSingle(c =>
            c.Estado == EstadoDocumento.Vencido && c.Descripcion.Contains("Empresa")
            && c.DocumentoId == documentoEmpresaId && c.TipoDocumentoId == tipoEmpresaId && c.FechaVencimiento == fechaVencimiento);
    }

    [Fact]
    public async Task Un_documento_bloqueante_ausente_deja_el_Centro_en_Faltante_y_no_Bloqueado_porque_Bloqueado_es_del_Trabajador()
    {
        Guid tipoBloqueanteId;
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddYears(1))));

            var tipoBloqueante = new TipoDocumento("Formulario de acceso", null, false, 3, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
            contexto.TiposDocumento.Add(tipoBloqueante);
            await contexto.SaveChangesAsync();
            tipoBloqueanteId = tipoBloqueante.Id;

            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoBloqueanteId, _centroId, incluido: true, bloqueaAcceso: true));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        // «Bloqueado» es un estado del Trabajador, nunca del Centro (2026-10-03; tampoco por la plataforma del Cliente
        // empresarial, 2026-10-04). Quién no puede entrar y por qué lo dice IEvaluacionDeAccesoPorCentroService, no este semáforo.
        resultado.Estado.Should().Be(EstadoCentro.Faltante);
        resultado.Causas.Should().ContainSingle(c =>
            c.Estado == EstadoDocumento.Faltante
            && c.Descripcion.Contains("Formulario de acceso") && c.Descripcion.Contains("Ana García"));
    }

    [Fact]
    public async Task Subir_el_documento_bloqueante_deja_de_bloquear_el_centro()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddYears(1))));

            var tipoBloqueante = new TipoDocumento("Formulario de acceso", null, false, 3, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
            contexto.TiposDocumento.Add(tipoBloqueante);
            await contexto.SaveChangesAsync();

            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoBloqueante.Id, _centroId, incluido: true, bloqueaAcceso: true));
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, tipoBloqueante.Id, DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddYears(1))));
            await contexto.SaveChangesAsync();
        }

        var resultado = await CalcularAsync();

        resultado.Estado.Should().Be(EstadoCentro.Vigente);
        resultado.Causas.Should().BeEmpty();
    }

    /// <summary>
    /// Premisa del estado vacío de "Centros con menor cumplimiento"
    /// (<c>ObtenerCatalogoKpisQuery</c> solo lista centros con
    /// <c>Requeridos &gt; 0</c>): un centro que SÍ pide documentación de
    /// trabajador —aquí por el valor general del tipo, sin fila propia, igual
    /// que el centro del fixture— pero sin ningún trabajador asignado cuenta
    /// cero requeridos, exactamente como uno que no pide nada. Por eso el
    /// dashboard no puede atribuir la lista vacía a que ningún centro la pida.
    /// Se ejercitan las dos salidas: el retorno temprano cuando ninguno de los
    /// centros tiene asignaciones y el bucle cuando solo algunos las tienen.
    /// </summary>
    [Fact]
    public async Task Un_centro_que_pide_documentacion_de_trabajador_pero_sin_trabajadores_asignados_cuenta_cero_requeridos()
    {
        Guid centroSinTrabajadoresId;
        await using (var contexto = CrearContexto())
        {
            var centroConTrabajadores = await contexto.Centros.SingleAsync(c => c.Id == _centroId);
            var centroSinTrabajadores = new Centro(centroConTrabajadores.ClienteId, _empresaId, "Centro sin trabajadores");
            contexto.Centros.Add(centroSinTrabajadores);
            await contexto.SaveChangesAsync();
            centroSinTrabajadoresId = centroSinTrabajadores.Id;
        }

        await using var lectura = CrearContexto();
        var servicio = new CalculoEstadoCentroService(lectura, lectura, lectura, lectura, lectura, lectura);

        var ambos = await servicio.CalcularCumplimientoAsync([_centroId, centroSinTrabajadoresId], CancellationToken.None);
        var soloElVacio = await servicio.CalcularCumplimientoAsync([centroSinTrabajadoresId], CancellationToken.None);

        ambos[_centroId].Requeridos.Should().Be(1,
            "control positivo: el mismo tipo, sin fila de centro, se pide y se evalúa en cuanto hay un trabajador asignado");
        ambos[centroSinTrabajadoresId].Requeridos.Should().Be(0,
            "sin trabajadores asignados no hay pares trabajador × tipo que evaluar, aunque el centro pida el tipo");
        soloElVacio[centroSinTrabajadoresId].Requeridos.Should().Be(0,
            "misma respuesta por el retorno temprano, cuando ningún centro pedido tiene asignaciones");
    }

    /// <summary>
    /// P1-D3: el índice (TrabajadorId, TipoDocumentoId) de Documentos no es
    /// único y la subida manual, la subida masiva con IA y la generación por
    /// plantilla insertan sin mirar si ya hay otro del mismo tipo — la
    /// renovación típica deja el vencido y añade el nuevo. Antes, el
    /// <c>ToDictionary</c> por par lanzaba ArgumentException y tumbaba el
    /// cumplimiento del Centro entero. Ahora manda el documento que elige
    /// <c>DocumentoEfectivo</c> para representar al tipo: el válido hoy
    /// antes que el vencido.
    /// </summary>
    [Fact]
    public async Task Con_el_vencido_y_su_renovacion_del_mismo_tipo_cuenta_al_dia_por_el_vigente()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-10))));
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, hoy.AddDays(-5), VigenciaDocumento.VenceEl(hoy.AddYears(1))));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var servicio = new CalculoEstadoCentroService(lectura, lectura, lectura, lectura, lectura, lectura);

        var cumplimiento = await servicio.CalcularCumplimientoAsync([_centroId], CancellationToken.None);

        cumplimiento[_centroId].Requeridos.Should().Be(1, "dos copias del mismo tipo siguen siendo un único requisito del Trabajador");
        cumplimiento[_centroId].AlDia.Should().Be(1, "manda la renovación vigente, no la copia vencida");
    }

    /// <summary>
    /// El orden único (<c>DocumentoEfectivo</c>) llega al servicio con todos sus campos: entre dos copias válidas hoy del
    /// mismo tipo manda la de emisión más reciente aunque tenga la vigencia sin confirmar (no conforme, decisión
    /// 2026-10-03), no la que vence más tarde ni la «confirmada». Sin <c>CreadoEnUtc</c> o <c>FechaEmision</c> en la
    /// proyección el resultado cambia.
    /// </summary>
    [Fact]
    public async Task Entre_dos_validas_hoy_del_mismo_tipo_manda_la_de_emision_mas_reciente()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = CrearContexto())
        {
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, hoy.AddDays(-200), VigenciaDocumento.VenceEl(hoy.AddDays(300))));
            contexto.Documentos.Add(Documento.DeTrabajador(
                _trabajadorId, _tipoDocumentoObligatorioId, hoy.AddDays(-5), VigenciaDocumento.SinConfirmar));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var servicio = new CalculoEstadoCentroService(lectura, lectura, lectura, lectura, lectura, lectura);

        var cumplimiento = await servicio.CalcularCumplimientoAsync([_centroId], CancellationToken.None);

        cumplimiento[_centroId].Requeridos.Should().Be(1);
        cumplimiento[_centroId].AlDia.Should().Be(0, "la copia de emisión más reciente es la efectiva y su vigencia está sin confirmar");
    }

    /// <summary>Acreditación del documento vigente del Trabajador contra un canal de plataforma del Centro indicado.</summary>
    private async Task SembrarAcreditacionEnAsync(
        Guid centroId, Guid trabajadorId, Action<AcreditacionDocumentoPlataforma> configurar, Guid? tipoDocumentoId = null)
    {
        await using var contexto = CrearContexto();

        var documento = Documento.DeTrabajador(
            trabajadorId, tipoDocumentoId ?? _tipoDocumentoObligatorioId,
            DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddYears(1)));
        contexto.Documentos.Add(documento);

        var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma de prueba");
        contexto.ProveedoresPlataformaCae.Add(proveedor);
        await contexto.SaveChangesAsync();

        var canal = CanalGestionDocumental.DePlataforma(centroId, "Acceso de prueba", proveedor.Id, null, null, null);
        contexto.CanalesGestionDocumental.Add(canal);
        await contexto.SaveChangesAsync();

        var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
        configurar(acreditacion);
        contexto.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await contexto.SaveChangesAsync();
    }

    private async Task<ResultadoEstadoCentro> CalcularAsync()
    {
        await using var contexto = CrearContexto();
        var servicio = new CalculoEstadoCentroService(contexto, contexto, contexto, contexto, contexto, contexto);

        var resultados = await servicio.CalcularAsync([_centroId], CancellationToken.None);
        return resultados[_centroId];
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
