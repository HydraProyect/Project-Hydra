using CaeManager.Application.Centros;
using CaeManager.Application.Documentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Trabajadores;

public class EstadoDocumentalConFaltantesTests
{
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly ConfiguracionQueryContextFalso _configuracion = new();
    private readonly AsignacionesQueryContextFalso _asignaciones = new();
    private readonly ProyectosQueryContextFalso _proyectos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly CentrosQueryContextFalso _centros = new();
    private readonly Empresa _empresa = new("Montajes");
    private readonly TipoDocumento _exigido = new("Aptitud médica", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.Si);
    private readonly TipoDocumento _otro = new("Formación", 12, true, 1, AmbitoAplicacion.Trabajador, RequisitoDocumental.No);
    private readonly Centro _centro;

    public EstadoDocumentalConFaltantesTests()
    {
        _empresas.ListaEmpresas.Add(_empresa);
        _centro = new Centro(Guid.NewGuid(), _empresa.Id, "Obra");
        _centros.ListaCentros.Add(_centro);
        _tipos.ListaTiposDocumento.AddRange([_exigido, _otro]);
        // El Centro marca el Tipo como obligatorio («bloquea el acceso»): solo eso tiñe el estado (decisión del 2026-10-23).
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_exigido.Id, _centro.Id, incluido: true, bloqueaAcceso: true));
        _configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 15));
        Agregar("Correcto", 90, conExigido: true);
        Agregar("Faltante", 90);
        Agregar("Vencido", -1);
        Agregar("Urgente", 2);
        Agregar("Sin documentos", null);
    }

    private Trabajador Agregar(string nombre, int? dias, bool conExigido = false)
    {
        var trabajador = Trabajador.DeEmpresa(_empresa.Id, nombre, "Apellido", "12345678Z");
        _trabajadores.ListaTrabajadores.Add(trabajador);
        _asignaciones.ListaAsignaciones.Add(new Asignacion(trabajador.Id, _centro.Id, DiaDeNegocio.Hoy()));
        if (dias is { } d)
            _documentos.ListaDocumentos.Add(Documento.DeTrabajador(trabajador.Id, _otro.Id,
                DiaDeNegocio.Hoy().AddDays(-100), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(d))));
        if (conExigido)
            _documentos.ListaDocumentos.Add(Documento.DeTrabajador(trabajador.Id, _exigido.Id,
                DiaDeNegocio.Hoy().AddDays(-100), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy().AddDays(90))));
        return trabajador;
    }

    private ObtenerTrabajadoresQueryHandler Handler(AlcanceDatosServiceFalso? alcance = null) =>
        new(_empresas, _trabajadores, _documentos, _configuracion, alcance ?? new(),
            new CalculoEstadoDocumentalService(_documentos, _configuracion, _tipos), _asignaciones, _proyectos,
            new RequisitosObligatoriosIncumplidosService(
                new EvaluacionDeAccesoPorCentroService(_centros, _tipos, _trabajadores, _asignaciones, _documentos, alcance ?? new()),
                _asignaciones, _trabajadores, _tipos));

    [Theory]
    [InlineData("Faltante", 3)]
    [InlineData("Vigente,SinCaducidad", 1)]
    [InlineData("Vencido", 1)]
    [InlineData("Urgente,Proximo", 0)]
    [InlineData("Faltante,Vencido", 4)]
    public async Task El_filtro_y_la_franja_incluyen_los_faltantes_antes_de_paginar(string filtro, int total)
    {
        var resultado = await Handler().Handle(new(null, EstadoDocumental: filtro, TamanoPagina: 1,
            ConDesgloseDocumental: true, ConRecuentosPorEstado: true), CancellationToken.None);

        resultado.TotalElementos.Should().Be(total);
        resultado.Elementos.Should().HaveCount(Math.Min(total, 1));
        resultado.RecuentosPorEstado![nameof(EstadoDocumento.Faltante)].Should().Be(3);
        resultado.RecuentosPorEstado[nameof(EstadoDocumento.Vencido)].Should().Be(1);
        resultado.RecuentosPorEstado[nameof(EstadoDocumento.Vigente)].Should().Be(1);
        resultado.RecuentosPorEstado.Values.Sum().Should().Be(5);
        if (filtro == "Vigente,SinCaducidad")
            resultado.Elementos.Should().OnlyContain(t => t.DocumentosFaltantes.Count == 0);
        if (filtro == "Faltante")
            resultado.Elementos.Should().OnlyContain(t => t.DocumentosFaltantes.Contains("Aptitud médica"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task El_orden_situa_vencidos_luego_faltantes_y_finalmente_sin_incidencias(bool descendente)
    {
        string[] esperado = ["Vencido", "Faltante", "Sin documentos", "Urgente", "Correcto"];
        if (descendente)
            esperado = ["Correcto", "Faltante", "Sin documentos", "Urgente", "Vencido"];

        var handler = Handler();
        var nombres = new List<string>();
        for (var pagina = 1; pagina <= 5; pagina++)
        {
            var resultado = await handler.Handle(new(null, Pagina: pagina, TamanoPagina: 1,
                OrdenarPor: nameof(TrabajadorListaDto.EstadoDocumental), Descendente: descendente), CancellationToken.None);
            nombres.Add(resultado.Elementos.Single().Nombre);
        }
        nombres.Should().Equal(esperado);
    }

    [Fact]
    public async Task La_franja_respeta_los_demas_filtros_y_la_fila_individual()
    {
        var faltante = _trabajadores.ListaTrabajadores.Single(t => t.Nombre == "Faltante");
        var resultado = await Handler().Handle(new(null, TrabajadorId: faltante.Id,
            ConRecuentosPorEstado: true), CancellationToken.None);
        resultado.TotalElementos.Should().Be(1);
        resultado.RecuentosPorEstado![nameof(EstadoDocumento.Faltante)].Should().Be(1);
        resultado.RecuentosPorEstado.Values.Sum().Should().Be(1);
    }

    [Fact]
    public async Task La_regla_compartida_excluye_requisitos_desmarcados_no_obligatorios_y_centros_sin_gestion_cae()
    {
        _tipos.ListaTiposDocumentoCentros.Clear();
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_exigido.Id, _centro.Id, incluido: false));
        var sinRequisito = await Handler().Handle(new(null, EstadoDocumental: "Faltante"), CancellationToken.None);
        sinRequisito.TotalElementos.Should().Be(0);

        // Exigido por el Centro (y por el criterio global, RequisitoDocumental.Si) pero sin la marca «bloquea el acceso»:
        // no bloquea el Centro, así que no genera alarma ni tiñe a nadie de Pendiente.
        _tipos.ListaTiposDocumentoCentros.Clear();
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_exigido.Id, _centro.Id, incluido: true, bloqueaAcceso: false));
        var noObligatorio = await Handler().Handle(new(null, ConRecuentosPorEstado: true, TamanoPagina: 10), CancellationToken.None);
        noObligatorio.RecuentosPorEstado![nameof(EstadoDocumento.Faltante)].Should().Be(0);
        noObligatorio.Elementos.Should().OnlyContain(t => t.DocumentosFaltantes.Count == 0 && t.EstadoDocumental != EstadoDocumento.Faltante);

        _tipos.ListaTiposDocumentoCentros.Clear();
        _tipos.ListaTiposDocumentoCentros.Add(new TipoDocumentoCentro(_exigido.Id, _centro.Id, incluido: true, bloqueaAcceso: true));
        _centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
        var sinGestion = await Handler().Handle(new(null, EstadoDocumental: "Faltante"), CancellationToken.None);
        sinGestion.TotalElementos.Should().Be(0);
    }

    [Fact]
    public async Task Los_voluntarios_inactivos_no_generan_faltantes_en_la_franja()
    {
        foreach (var trabajador in _trabajadores.ListaTrabajadores)
            trabajador.CambiarTipoContratacion(TipoContratacion.Voluntario);
        var resultado = await Handler().Handle(new(null, EstadoDocumental: "Faltante",
            ConRecuentosPorEstado: true), CancellationToken.None);
        resultado.TotalElementos.Should().Be(0);
        resultado.RecuentosPorEstado![nameof(EstadoDocumento.Faltante)].Should().Be(0);
    }
}
