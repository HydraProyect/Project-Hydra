using CaeManager.Application.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Subcontratas;

/// <summary>
/// Primer nivel de "Subcontrata 360": % de cumplimiento y recuentos de sus Trabajadores en la
/// fila de <c>/subcontratas</c>, calculados por <see cref="CalculoEstadoSubcontrataService"/>
/// — mismo alcance que el badge de Centro 360 —, y el estado de la fila, que es el de la
/// documentación de Empresa de la Subcontrata con los requisitos obligatorios de Centro y sus
/// Trabajadores bloqueados como recuento aparte (decisión D-1).
/// </summary>
public class ObtenerSubcontratasQueryCumplimientoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _subcontrataId;
    private Guid _centroId;
    private Guid _tipoObligatorioId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente Cumplimiento Sub S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Cumplimiento Sub S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Cumplimiento Sub");
        contexto.Centros.Add(centro);

        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Cumplimiento S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.Add(subcontrata);

        var tipoObligatorio = new TipoDocumento("EPIs", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipoObligatorio);
        await contexto.SaveChangesAsync();

        _subcontrataId = subcontrata.Id;
        _centroId = centro.Id;
        _tipoObligatorioId = tipoObligatorio.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Sin_trabajadores_con_requisitos_el_cumplimiento_es_nulo()
    {
        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.CumplimientoPorcentaje.Should().BeNull();
        fila.Recuentos.TotalVencidas.Should().Be(0);
        fila.Recuentos.TotalProximas.Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_obligatorio_faltante_cuenta_como_incumplimiento_al_0_por_ciento()
    {
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Sara", "Faltante", "12345678Z");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, _centroId, DiaDeNegocio.Hoy()));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.CumplimientoPorcentaje.Should().Be(0);
        fila.Recuentos.TotalVencidas.Should().Be(1);
    }

    [Fact]
    public async Task Con_el_vencido_y_su_renovacion_del_mismo_tipo_cuenta_el_vigente_y_no_falla()
    {
        // El índice (TrabajadorId, TipoDocumentoId) de Documento no es único: el
        // vencido sigue ahí cuando se sube la renovación. Se inserta primero la
        // renovación para que «el último leído» no acierte por casualidad.
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Iris", "Renovada", "77189989B");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, _centroId, hoy));
            contexto.Documentos.Add(Documento.DeTrabajador(trabajador.Id, _tipoObligatorioId, hoy.AddDays(-1), VigenciaDocumento.VenceEl(hoy.AddYears(1))));
            await contexto.SaveChangesAsync();
            contexto.Documentos.Add(Documento.DeTrabajador(trabajador.Id, _tipoObligatorioId, hoy.AddYears(-2), VigenciaDocumento.VenceEl(hoy.AddDays(-10))));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.CumplimientoPorcentaje.Should().Be(100);
        fila.Recuentos.TotalVencidas.Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_vigente_cuenta_como_cumplimiento_al_100_por_ciento()
    {
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Julio", "Aldia", "77189989B");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, _centroId, DiaDeNegocio.Hoy()));
            contexto.Documentos.Add(Documento.DeTrabajador(
                trabajador.Id, _tipoObligatorioId, DiaDeNegocio.Hoy(), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddYears(1))));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.CumplimientoPorcentaje.Should().Be(100);
        fila.Recuentos.TotalVencidas.Should().Be(0);
    }

    [Fact]
    public async Task El_nivel_de_servicio_viaja_en_la_fila_de_la_lista()
    {
        await using (var contexto = CrearContexto())
        {
            var subcontrata = await contexto.Empresas.SingleAsync(e => e.Id == _subcontrataId);
            subcontrata.CambiarNivelServicioComoSubcontrata(NivelServicioSubcontrata.Supervisada.ToString());
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.NivelServicio.Should().Be(NivelServicioSubcontrata.Supervisada);
    }

    [Fact]
    public async Task Un_documento_en_estado_Urgente_aparece_en_Proximas()
    {
        // Mismo defecto que tenía ObtenerCentrosQuery.Desglosar (D-7, piloto
        // Outbound), ahora corregido también aquí: Urgente (dentro del
        // umbral rojo de 15 días, sin llegar a vencer) no es una tercera
        // casilla fuera del recuento. Se agrupa con Proximas, no con
        // Vencidas: el documento aún no venció, y ambos buckets se leen como
        // texto literal ("N vencido(s)") en la UI — meterlo en Vencidas
        // afirmaría una fecha vencida que no lo está (hallazgo de Codex,
        // oleada 3 sobre esta misma PR).
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Ines", "Urgente", "99887766P");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(trabajador.Id, _centroId, DiaDeNegocio.Hoy()));
            contexto.Documentos.Add(Documento.DeTrabajador(
                trabajador.Id, _tipoObligatorioId,
                DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(10))));
            await contexto.SaveChangesAsync();
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.Recuentos.TotalProximas.Should().Be(1,
            "el documento aún no venció: Vencidas se lee como texto literal en la UI y Urgente no es un vencimiento real");
        fila.Recuentos.TotalVencidas.Should().Be(0,
            "Urgente no es Vencido: el recuento de vencidas no debe mezclar severidad de color con vencimiento real");
    }

    /// <summary>
    /// Listados 3/7: la incidencia de la ventana de contexto se pulsa y abre su corrección. La que
    /// tiene Documento lo renueva; la que no, da de alta el que falta, y para eso necesita saber de
    /// qué Trabajador. Los dos caminos dependen de estos identificadores.
    /// </summary>
    [Fact]
    public async Task Cada_incidencia_identifica_a_su_trabajador_y_la_que_tiene_documento_tambien_el_documento()
    {
        Guid conDocumentoId, sinDocumentoId, documentoId;
        await using (var contexto = CrearContexto())
        {
            var conDocumento = Trabajador.DeSubcontrata(_subcontrataId, "Ines", "Urgente", "99887766P");
            var sinDocumento = Trabajador.DeSubcontrata(_subcontrataId, "Sara", "Faltante", "12345678Z");
            contexto.Trabajadores.AddRange(conDocumento, sinDocumento);
            await contexto.SaveChangesAsync();

            contexto.Asignaciones.Add(new Asignacion(conDocumento.Id, _centroId, DiaDeNegocio.Hoy()));
            contexto.Asignaciones.Add(new Asignacion(sinDocumento.Id, _centroId, DiaDeNegocio.Hoy()));
            var documento = Documento.DeTrabajador(
                conDocumento.Id, _tipoObligatorioId,
                DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(10)));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();

            (conDocumentoId, sinDocumentoId, documentoId) = (conDocumento.Id, sinDocumento.Id, documento.Id);
        }

        var resultado = await EjecutarAsync();

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        var proxima = fila.Recuentos.Proximas.Should().ContainSingle().Subject;
        proxima.DocumentoId.Should().Be(documentoId);
        proxima.TipoDocumentoId.Should().Be(_tipoObligatorioId);
        proxima.TrabajadorId.Should().Be(conDocumentoId);

        var faltante = fila.Recuentos.Vencidas.Should().ContainSingle().Subject;
        faltante.DocumentoId.Should().BeNull("no hay Documento que renovar: se da de alta el que falta");
        faltante.TipoDocumentoId.Should().Be(_tipoObligatorioId);
        faltante.TrabajadorId.Should().Be(sinDocumentoId);
    }

    [Fact]
    public async Task Un_documento_faltante_de_un_trabajador_fuera_de_cartera_no_cuenta_en_el_cumplimiento()
    {
        Guid trabajadorFueraDeCarteraId;
        await using (var contexto = CrearContexto())
        {
            var trabajador = Trabajador.DeSubcontrata(_subcontrataId, "Nora", "Nocartera", "55667788Z");
            contexto.Trabajadores.Add(trabajador);
            await contexto.SaveChangesAsync();
            trabajadorFueraDeCarteraId = trabajador.Id;

            contexto.Asignaciones.Add(new Asignacion(trabajadorFueraDeCarteraId, _centroId, DiaDeNegocio.Hoy()));
            await contexto.SaveChangesAsync();
        }

        // Cartera vacía a propósito (ningún trabajador visible): mismo
        // criterio que ObtenerTrabajadoresQuery — un requisito faltante de un
        // trabajador fuera de cartera no debe aparecer en el anillo de
        // cumplimiento ni en los recuentos de la fila.
        var resultado = await EjecutarAsync(new AlcanceDatosServiceFalso(trabajadorIds: []));

        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.CumplimientoPorcentaje.Should().BeNull();
        fila.Recuentos.TotalVencidas.Should().Be(0);
    }

    /// <summary>
    /// Sin confirmar no es incidencia de color (no entra en Vencidas ni en Próximas, como antes) y viaja aparte. El de un
    /// Trabajador va en sus recuentos y no decide el estado de la Subcontrata; el de un documento de Empresa de la Subcontrata sí
    /// lo decide cuando no hay nada peor (decisión D-1).
    /// </summary>
    [Fact]
    public async Task Un_documento_sin_confirmar_de_un_Trabajador_no_decide_el_estado_y_el_de_la_Subcontrata_si()
    {
        Guid trabajadorId;
        await using (var contexto = CrearContexto())
        {
            trabajadorId = await AnadirTrabajadorAsync(contexto, _subcontrataId, "Sara", "Sinconfirmar", "11223344B", VigenciaDocumento.SinConfirmar);
        }

        var fila = (await EjecutarAsync()).Elementos.Should().ContainSingle().Subject;

        fila.Recuentos.TotalVencidas.Should().Be(0);
        fila.Recuentos.TotalProximas.Should().Be(0);
        var sinConfirmar = fila.Recuentos.SinConfirmar.Should().ContainSingle().Subject;
        sinConfirmar.Estado.Should().Be(EstadoDocumento.SinConfirmar);
        sinConfirmar.TrabajadorId.Should().Be(trabajadorId);
        sinConfirmar.DocumentoId.Should().NotBeNull("es el documento cuya vigencia hay que confirmar");
        fila.EstadoDocumental.Should().Be(EstadoDocumento.SinCaducidad,
            "la Subcontrata no tiene documentos de Empresa: lo de sus Trabajadores no la pinta (decisión D-1)");
        fila.TrabajadoresBloqueados.Should().Be(0, "ningún Centro marca obligatorio ese Tipo");
        fila.CumplimientoPorcentaje.Should().Be(0, "sin confirmar no es conforme (decisión del 2026-10-03)");

        await using (var contexto = CrearContexto())
        {
            var tipoEmpresa = new TipoDocumento("Seguro de responsabilidad civil", null, false, 1, AmbitoAplicacion.Empresa);
            contexto.TiposDocumento.Add(tipoEmpresa);
            await contexto.SaveChangesAsync();
            contexto.Documentos.Add(Documento.DeEmpresa(_subcontrataId, tipoEmpresa.Id, DiaDeNegocio.Hoy().AddDays(-5), VigenciaDocumento.SinConfirmar));
            await contexto.SaveChangesAsync();
        }

        var conDocumentoDeEmpresa = (await EjecutarAsync()).Elementos.Should().ContainSingle().Subject;
        conDocumentoDeEmpresa.EstadoDocumental.Should().Be(EstadoDocumento.SinConfirmar, "su documento de Empresa decide su estado");
        conDocumentoDeEmpresa.Incidencias.Should().ContainSingle().Which.Estado.Should().Be(EstadoDocumento.SinConfirmar);

        // Quien lee solo las causas (Subcontrata 360, supervisión) sigue sin recibirlo como incidencia.
        await using var lectura = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();
        var causas = await new CalculoEstadoSubcontrataService(lectura, lectura, lectura, lectura, lectura, lectura, alcance, lectura, lectura)
            .CalcularAsync([_subcontrataId], CancellationToken.None);
        causas[_subcontrataId].Should().BeEmpty();
    }

    /// <summary>
    /// Franja de estado (decisión D-1): seis Subcontratas, una por estado de su documentación de Empresa. El rojo es solo el
    /// documento de Empresa que el Centro donde trabajan sus Trabajadores marca obligatorio; los Trabajadores bloqueados de la
    /// sexta no la ponen en rojo y van como recuento aparte. El filtro acepta varios estados, los recuentos no cuentan el
    /// propio filtro de estado y la paginación se aplica DESPUÉS de filtrar (paginar antes dejaría páginas a medias y un total
    /// falso).
    /// </summary>
    [Fact]
    public async Task El_filtro_de_estado_filtra_cuenta_y_pagina_sobre_el_estado_de_la_documentacion_de_Empresa()
    {
        var hoy = DiaDeNegocio.Hoy();
        Guid conBloqueadosId;
        await using (var contexto = CrearContexto())
        {
            var tipoEmpresaId = await MarcarObligatoriosAsync(contexto);

            // «Subcontrata Cumplimiento S.L.» (la de la siembra): opera en el Centro y su documento obligatorio venció → Vencido.
            await AnadirTrabajadorAsync(contexto, _subcontrataId, "Vera", "Aldia", "10000001S", VigenciaDocumento.VenceEl(hoy.AddYears(1)));
            await AnadirDocumentoDeEmpresaAsync(contexto, _subcontrataId, tipoEmpresaId, VigenciaDocumento.VenceEl(hoy.AddDays(-90)));

            var falta = Empresa.CrearComoSubcontrata("Subcontrata A Falta S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
            var urgente = Empresa.CrearComoSubcontrata("Subcontrata B Urgente S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
            var sinConfirmar = Empresa.CrearComoSubcontrata("Subcontrata C Sin Confirmar S.L.", null, NivelServicioSubcontrata.Supervisada.ToString());
            var alCorriente = Empresa.CrearComoSubcontrata("Subcontrata D Al Corriente S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
            var conBloqueados = Empresa.CrearComoSubcontrata("Subcontrata E Con Bloqueados S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.AddRange(falta, urgente, sinConfirmar, alCorriente, conBloqueados);
            await contexto.SaveChangesAsync();
            conBloqueadosId = conBloqueados.Id;

            // Opera en el Centro sin el documento obligatorio → Faltante (se rotula Pendiente).
            await AnadirTrabajadorAsync(contexto, falta.Id, "Fede", "Aldia", "10000006C", VigenciaDocumento.VenceEl(hoy.AddYears(1)));

            // Sin Trabajadores en el Centro: el obligatorio no les aplica y manda el peor de sus documentos de Empresa.
            await AnadirDocumentoDeEmpresaAsync(contexto, urgente.Id, tipoEmpresaId, VigenciaDocumento.VenceEl(hoy.AddDays(10)));
            await AnadirDocumentoDeEmpresaAsync(contexto, sinConfirmar.Id, tipoEmpresaId, VigenciaDocumento.SinConfirmar);
            await AnadirDocumentoDeEmpresaAsync(contexto, alCorriente.Id, tipoEmpresaId, VigenciaDocumento.VenceEl(hoy.AddYears(1)));

            // Su documentación de Empresa está al día y dos de sus Trabajadores no tienen el EPI obligatorio del Centro.
            await AnadirDocumentoDeEmpresaAsync(contexto, conBloqueados.Id, tipoEmpresaId, VigenciaDocumento.VenceEl(hoy.AddYears(1)));
            await AnadirTrabajadorAsync(contexto, conBloqueados.Id, "Blas", "Bloqueado", "10000007K", vigencia: null);
            await AnadirTrabajadorAsync(contexto, conBloqueados.Id, "Bea", "Bloqueada", "10000008E", vigencia: null);
            await AnadirTrabajadorAsync(contexto, conBloqueados.Id, "Dani", "Aldia", "10000009T", VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        }

        var todas = await EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null, ConRecuentosPorEstado: true));

        todas.TotalElementos.Should().Be(6);
        todas.RecuentosPorEstado.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Vencido"] = 1,
            ["Faltante"] = 1,
            ["Urgente"] = 1,
            ["Proximo"] = 0,
            ["SinConfirmar"] = 1,
            ["Vigente"] = 2,
            ["SinCaducidad"] = 0
        });
        var filas = todas.Elementos.ToDictionary(s => s.RazonSocial);
        filas["Subcontrata A Falta S.L."].DocumentosFaltantes.Should().Equal("Seguro de responsabilidad civil");
        filas["Subcontrata E Con Bloqueados S.L."].EstadoDocumental.Should().Be(EstadoDocumento.Vigente,
            "sus Trabajadores bloqueados no ponen en rojo a la Subcontrata (decisión D-1)");
        filas["Subcontrata E Con Bloqueados S.L."].TrabajadoresBloqueados.Should().Be(2);
        filas.Values.Where(s => s.Id != conBloqueadosId).Should().OnlyContain(s => s.TrabajadoresBloqueados == 0);

        var porVencer = await EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null, EstadoDocumental: "Urgente,Proximo", ConRecuentosPorEstado: true));

        porVencer.Elementos.Select(s => s.RazonSocial).Should().Equal("Subcontrata B Urgente S.L.");
        porVencer.TotalElementos.Should().Be(1);
        porVencer.RecuentosPorEstado.Should().BeEquivalentTo(todas.RecuentosPorEstado, "los recuentos no cuentan el filtro de estado");

        // Dos estados y página de 1: la segunda página es la segunda fila FILTRADA, por razón social.
        var pagina2 = await EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null, Pagina: 2, TamanoPagina: 1, EstadoDocumental: "Vencido,SinConfirmar"));

        pagina2.TotalElementos.Should().Be(2);
        pagina2.Elementos.Select(s => s.RazonSocial).Should().Equal("Subcontrata Cumplimiento S.L.");
        pagina2.RecuentosPorEstado.Should().BeNull("no se pidieron");

        (await EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null, EstadoDocumental: "Faltante"))).Elementos
            .Select(s => s.RazonSocial).Should().Equal("Subcontrata A Falta S.L.");

        // Con los demás filtros: el nivel de servicio se aplica antes de contar.
        var supervisadas = await EjecutarAsync(new ObtenerSubcontratasQuery(
            Busqueda: null, NivelServicio: NivelServicioSubcontrata.Supervisada, ConRecuentosPorEstado: true));

        supervisadas.RecuentosPorEstado!.Where(par => par.Value > 0).Select(par => (par.Key, par.Value)).Should().Equal(("SinConfirmar", 1));

        // Un valor que no es un estado no filtra: la coordenada de la URL no inventa una lista vacía.
        (await EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null, EstadoDocumental: "Inventado"))).TotalElementos.Should().Be(6);
    }

    /// <summary>
    /// El recuento de Trabajadores bloqueados (decisión D-1) es el de los Trabajadores dados de alta con la Subcontrata
    /// (<c>SubcontrataId</c>) que el usuario ve: uno fuera de su cartera no se cuenta, igual que no sale en la fila. Un
    /// voluntario inactivo sí sale en la fila, pero no cuenta como bloqueado.
    /// </summary>
    [Fact]
    public async Task Los_Trabajadores_bloqueados_son_solo_los_de_la_Subcontrata_que_el_usuario_ve()
    {
        Guid visibleId;
        await using (var contexto = CrearContexto())
        {
            await MarcarObligatoriosAsync(contexto);
            visibleId = await AnadirTrabajadorAsync(contexto, _subcontrataId, "Vito", "Visible", "10000010R", vigencia: null);
            await AnadirTrabajadorAsync(contexto, _subcontrataId, "Otto", "Oculto", "10000011W", vigencia: null);

            // Voluntario sin ningún evento sin terminar: inactivo. Sin el documento obligatorio, pero el listado de hoy no le
            // aplica los requisitos de Centro, así que no cuenta como bloqueado.
            var voluntarioId = await AnadirTrabajadorAsync(contexto, _subcontrataId, "Valia", "Voluntaria", "10000012A", vigencia: null);
            var voluntario = await contexto.Trabajadores.SingleAsync(t => t.Id == voluntarioId);
            voluntario.CambiarTipoContratacion(TipoContratacion.Voluntario);
            await contexto.SaveChangesAsync();
        }

        var conTodos = (await EjecutarAsync()).Elementos.Should().ContainSingle().Subject;
        conTodos.Trabajadores.Should().HaveCount(3, "el voluntario inactivo sale en la fila");
        conTodos.TrabajadoresBloqueados.Should().Be(2, "el voluntario inactivo no cuenta como bloqueado");

        var conCartera = (await EjecutarAsync(new AlcanceDatosServiceFalso(trabajadorIds: [visibleId]))).Elementos.Should().ContainSingle().Subject;
        conCartera.TrabajadoresBloqueados.Should().Be(1, "el Trabajador fuera de la cartera no se cuenta");
        conCartera.Trabajadores.Select(t => t.Id).Should().Equal(visibleId);
        conCartera.EstadoDocumental.Should().Be(EstadoDocumento.Faltante,
            "la Subcontrata opera en el Centro sin el documento de Empresa obligatorio, vea o no a todos sus Trabajadores");
    }

    /// <summary>
    /// El camino con estado calcula para TODAS las filas, no solo para la página: tiene que respetar el mismo
    /// alcance que el camino paginado. Una Subcontrata fuera del alcance no aparece ni se cuenta.
    /// </summary>
    [Fact]
    public async Task El_camino_con_estado_no_devuelve_ni_cuenta_subcontratas_fuera_del_alcance()
    {
        Guid fueraDeAlcanceId;
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = CrearContexto())
        {
            // Las dos operan en el Centro con su documento de Empresa obligatorio vencido: las dos son Vencido.
            var tipoEmpresaId = await MarcarObligatoriosAsync(contexto);
            await AnadirTrabajadorAsync(contexto, _subcontrataId, "Vera", "Faltante", "10000001S", vigencia: null);
            await AnadirDocumentoDeEmpresaAsync(contexto, _subcontrataId, tipoEmpresaId, VigenciaDocumento.VenceEl(hoy.AddDays(-90)));

            var fuera = Empresa.CrearComoSubcontrata("Subcontrata Fuera De Alcance S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.Add(fuera);
            await contexto.SaveChangesAsync();
            fueraDeAlcanceId = fuera.Id;
            await AnadirTrabajadorAsync(contexto, fuera.Id, "Fidel", "Fuera", "10000005L", vigencia: null);
            await AnadirDocumentoDeEmpresaAsync(contexto, fuera.Id, tipoEmpresaId, VigenciaDocumento.VenceEl(hoy.AddDays(-90)));
        }

        var resultado = await EjecutarAsync(
            new ObtenerSubcontratasQuery(Busqueda: null, EstadoDocumental: "Vencido", ConRecuentosPorEstado: true),
            new AlcanceDatosServiceFalso(subcontrataIds: [_subcontrataId]));

        resultado.Elementos.Select(s => s.Id).Should().Equal(_subcontrataId);
        resultado.Elementos.Should().NotContain(s => s.Id == fueraDeAlcanceId);
        resultado.TotalElementos.Should().Be(1);
        resultado.RecuentosPorEstado!["Vencido"].Should().Be(1, "la que está fuera del alcance tampoco se cuenta en la franja");
        resultado.Elementos.Single().TrabajadoresBloqueados.Should().Be(1, "solo su Trabajador, no el de la Subcontrata fuera del alcance");
    }

    /// <summary>
    /// El Centro marca obligatorios el EPI de los Trabajadores (<see cref="_tipoObligatorioId"/>) y un documento de Empresa nuevo,
    /// «Seguro de responsabilidad civil», cuyo Id devuelve: lo que pone en rojo a una Subcontrata y a sus Trabajadores.
    /// </summary>
    private async Task<Guid> MarcarObligatoriosAsync(CaeManagerDbContext contexto)
    {
        var tipoEmpresa = new TipoDocumento("Seguro de responsabilidad civil", null, false, 2, AmbitoAplicacion.Empresa);
        contexto.TiposDocumento.Add(tipoEmpresa);
        await contexto.SaveChangesAsync();
        contexto.TiposDocumentoCentros.AddRange(
            new TipoDocumentoCentro(_tipoObligatorioId, _centroId, incluido: true, bloqueaAcceso: true),
            new TipoDocumentoCentro(tipoEmpresa.Id, _centroId, incluido: true, bloqueaAcceso: true));
        await contexto.SaveChangesAsync();
        return tipoEmpresa.Id;
    }

    private static async Task AnadirDocumentoDeEmpresaAsync(
        CaeManagerDbContext contexto, Guid empresaId, Guid tipoDocumentoId, VigenciaDocumento vigencia)
    {
        contexto.Documentos.Add(Documento.DeEmpresa(empresaId, tipoDocumentoId, DiaDeNegocio.Hoy().AddDays(-200), vigencia));
        await contexto.SaveChangesAsync();
    }

    /// <summary>Trabajador de la Subcontrata asignado al Centro; con <paramref name="vigencia"/> se le da el documento exigido.</summary>
    private async Task<Guid> AnadirTrabajadorAsync(
        CaeManagerDbContext contexto, Guid subcontrataId, string nombre, string apellidos, string dni, VigenciaDocumento? vigencia)
    {
        var hoy = DiaDeNegocio.Hoy();
        var trabajador = Trabajador.DeSubcontrata(subcontrataId, nombre, apellidos, dni);
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, _centroId, hoy));
        if (vigencia is { } conVigencia)
            contexto.Documentos.Add(Documento.DeTrabajador(trabajador.Id, _tipoObligatorioId, hoy.AddYears(-1), conVigencia));
        await contexto.SaveChangesAsync();
        return trabajador.Id;
    }

    private Task<CaeManager.Application.Common.ResultadoPaginado<SubcontrataListaDto>> EjecutarAsync() =>
        EjecutarAsync(new AlcanceDatosServiceFalso());

    private Task<CaeManager.Application.Common.ResultadoPaginado<SubcontrataListaDto>> EjecutarAsync(AlcanceDatosServiceFalso alcance) =>
        EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null), alcance);

    private async Task<CaeManager.Application.Common.ResultadoPaginado<SubcontrataListaDto>> EjecutarAsync(
        ObtenerSubcontratasQuery consulta, AlcanceDatosServiceFalso? alcance = null)
    {
        alcance ??= new AlcanceDatosServiceFalso();
        await using var contexto = CrearContexto();
        var servicio = new CalculoEstadoSubcontrataService(contexto, contexto, contexto, contexto, contexto, contexto, alcance, contexto, contexto);
        var handler = new ObtenerSubcontratasQueryHandler(contexto, alcance, servicio, contexto, new CalculoEstadoDocumentalService(contexto, contexto, contexto), RequisitosObligatoriosDePrueba.Servicio(contexto, alcance));

        return await handler.Handle(consulta, CancellationToken.None);
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
