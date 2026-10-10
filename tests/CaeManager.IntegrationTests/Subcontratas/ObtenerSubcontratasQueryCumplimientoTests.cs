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
/// Primer nivel de "Subcontrata 360": % de cumplimiento y recuentos de la
/// fila de <c>/subcontratas</c>, calculados por <see cref="CalculoEstadoSubcontrataService"/>
/// — mismo alcance que el badge de Centro 360, sin ambito Empresa (Documento
/// no tiene propietario Subcontrata).
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
    /// Sin confirmar no es incidencia de color (no entra en Vencidas ni en Próximas, como antes), pero
    /// viaja aparte y decide el estado documental de la fila cuando no hay nada peor.
    /// </summary>
    [Fact]
    public async Task Un_documento_sin_confirmar_no_es_vencido_ni_proximo_pero_decide_el_estado_de_la_fila()
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
        fila.EstadoDocumental.Should().Be(EstadoDocumento.SinConfirmar);
        fila.CumplimientoPorcentaje.Should().Be(0, "sin confirmar no es conforme (decisión del 2026-10-03)");

        // Quien lee solo las causas (Subcontrata 360, supervisión) sigue sin recibirlo como incidencia.
        await using var lectura = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();
        var causas = await new CalculoEstadoSubcontrataService(lectura, lectura, lectura, lectura, lectura, lectura, alcance)
            .CalcularAsync([_subcontrataId], CancellationToken.None);
        causas[_subcontrataId].Should().BeEmpty();
    }

    /// <summary>
    /// Franja de estado: cuatro Subcontratas, una por estado. El filtro acepta varios estados, los recuentos
    /// no cuentan el propio filtro de estado y la paginación se aplica DESPUÉS de filtrar (el estado no está
    /// persistido: paginar antes dejaría páginas a medias y un total falso).
    /// </summary>
    [Fact]
    public async Task El_filtro_de_estado_filtra_cuenta_y_pagina_sobre_el_estado_calculado()
    {
        var hoy = DiaDeNegocio.Hoy();
        await using (var contexto = CrearContexto())
        {
            // «Subcontrata Cumplimiento S.L.» (la de la siembra): un requisito sin documento → Vencido.
            await AnadirTrabajadorAsync(contexto, _subcontrataId, "Vera", "Faltante", "10000001S", vigencia: null);

            var urgente = Empresa.CrearComoSubcontrata("Subcontrata B Urgente S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
            var sinConfirmar = Empresa.CrearComoSubcontrata("Subcontrata C Sin Confirmar S.L.", null, NivelServicioSubcontrata.Supervisada.ToString());
            var alCorriente = Empresa.CrearComoSubcontrata("Subcontrata D Al Corriente S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.AddRange(urgente, sinConfirmar, alCorriente);
            await contexto.SaveChangesAsync();

            await AnadirTrabajadorAsync(contexto, urgente.Id, "Ugo", "Urgente", "10000002Q", VigenciaDocumento.VenceEl(hoy.AddDays(10)));
            await AnadirTrabajadorAsync(contexto, sinConfirmar.Id, "Sira", "Sinconfirmar", "10000003V", VigenciaDocumento.SinConfirmar);
            await AnadirTrabajadorAsync(contexto, alCorriente.Id, "Alba", "Aldia", "10000004H", VigenciaDocumento.VenceEl(hoy.AddYears(1)));
        }

        var todas = await EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null, ConRecuentosPorEstado: true));

        todas.TotalElementos.Should().Be(4);
        todas.RecuentosPorEstado.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Vencido"] = 1,
            ["Urgente"] = 1,
            ["Proximo"] = 0,
            ["SinConfirmar"] = 1,
            ["Vigente"] = 1,
            ["SinCaducidad"] = 0
        });

        var porVencer = await EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null, EstadoDocumental: "Urgente,Proximo", ConRecuentosPorEstado: true));

        porVencer.Elementos.Select(s => s.RazonSocial).Should().Equal("Subcontrata B Urgente S.L.");
        porVencer.TotalElementos.Should().Be(1);
        porVencer.RecuentosPorEstado.Should().BeEquivalentTo(todas.RecuentosPorEstado, "los recuentos no cuentan el filtro de estado");

        // Dos estados y página de 1: la segunda página es la segunda fila FILTRADA, por razón social.
        var pagina2 = await EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null, Pagina: 2, TamanoPagina: 1, EstadoDocumental: "Vencido,SinConfirmar"));

        pagina2.TotalElementos.Should().Be(2);
        pagina2.Elementos.Select(s => s.RazonSocial).Should().Equal("Subcontrata Cumplimiento S.L.");
        pagina2.RecuentosPorEstado.Should().BeNull("no se pidieron");

        // Con los demás filtros: el nivel de servicio se aplica antes de contar.
        var supervisadas = await EjecutarAsync(new ObtenerSubcontratasQuery(
            Busqueda: null, NivelServicio: NivelServicioSubcontrata.Supervisada, ConRecuentosPorEstado: true));

        supervisadas.RecuentosPorEstado!.Where(par => par.Value > 0).Select(par => (par.Key, par.Value)).Should().Equal(("SinConfirmar", 1));

        // Un valor que no es un estado no filtra: la coordenada de la URL no inventa una lista vacía.
        (await EjecutarAsync(new ObtenerSubcontratasQuery(Busqueda: null, EstadoDocumental: "Inventado"))).TotalElementos.Should().Be(4);
    }

    /// <summary>
    /// El camino con estado calcula para TODAS las filas, no solo para la página: tiene que respetar el mismo
    /// alcance que el camino paginado. Una Subcontrata fuera del alcance no aparece ni se cuenta.
    /// </summary>
    [Fact]
    public async Task El_camino_con_estado_no_devuelve_ni_cuenta_subcontratas_fuera_del_alcance()
    {
        Guid fueraDeAlcanceId;
        await using (var contexto = CrearContexto())
        {
            await AnadirTrabajadorAsync(contexto, _subcontrataId, "Vera", "Faltante", "10000001S", vigencia: null);

            var fuera = Empresa.CrearComoSubcontrata("Subcontrata Fuera De Alcance S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.Add(fuera);
            await contexto.SaveChangesAsync();
            fueraDeAlcanceId = fuera.Id;
            await AnadirTrabajadorAsync(contexto, fuera.Id, "Fidel", "Fuera", "10000005L", vigencia: null);
        }

        var resultado = await EjecutarAsync(
            new ObtenerSubcontratasQuery(Busqueda: null, EstadoDocumental: "Vencido", ConRecuentosPorEstado: true),
            new AlcanceDatosServiceFalso(subcontrataIds: [_subcontrataId]));

        resultado.Elementos.Select(s => s.Id).Should().Equal(_subcontrataId);
        resultado.Elementos.Should().NotContain(s => s.Id == fueraDeAlcanceId);
        resultado.TotalElementos.Should().Be(1);
        resultado.RecuentosPorEstado!["Vencido"].Should().Be(1, "la que está fuera del alcance tampoco se cuenta en la franja");
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
        var servicio = new CalculoEstadoSubcontrataService(contexto, contexto, contexto, contexto, contexto, contexto, alcance);
        var handler = new ObtenerSubcontratasQueryHandler(contexto, alcance, servicio);

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
