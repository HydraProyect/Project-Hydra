using CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;
using CaeManager.Application.BusquedaGlobal.Queries.ContarVencenEstaSemana;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.BusquedaGlobal;

/// <summary>
/// Los datos que el palette añade a lo que ya encontró (maqueta del Command Palette): el estado del Trabajador en una
/// búsqueda por DNI/NIE, la fila de más que decide «Ver más →» y el recuento «N documentos vencen esta semana» de
/// «Reclamar a {Empresa}». Lo que está fuera de la cartera no se calcula.
/// </summary>
public class PaletaDatosDeSujetoTests
{
    private const string Dni = "77189989B";

    private readonly CentrosQueryContextFalso _centros = new();
    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly DocumentosQueryContextFalso _documentos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly ProyectosQueryContextFalso _proyectos = new();
    private readonly MediatorFalso _mediador = new();
    private readonly Guid _empresaId = Guid.NewGuid();
    private int _siguienteDni = 10_000_000;

    /// <summary>DNI válido y distinto en cada llamada: el alta de Trabajador lo exige.</summary>
    private string OtroDni()
    {
        var numero = _siguienteDni++;
        return $"{numero:D8}{"TRWAGMYFPDXBNJZSQVHLCKE"[numero % 23]}";
    }

    private BuscarGlobalQueryHandler Buscador(IAlcanceDatosService alcance) =>
        new(_centros, _empresas, _trabajadores, _documentos, _tipos, new VehiculosVacios(), _proyectos, alcance, _mediador);

    [Fact]
    public async Task Por_dni_el_trabajador_lleva_el_estado_del_listado_de_trabajadores()
    {
        var trabajador = Trabajador.DeEmpresa(_empresaId, "Ana", "Garcia", Dni);
        _trabajadores.ListaTrabajadores.Add(trabajador);
        _mediador.Respuesta = new ResultadoPaginado<TrabajadorListaDto>(
        [
            new TrabajadorListaDto(trabajador.Id, "Ana", "Garcia", Dni, "Montajes Ebro S.L.", EstadoDocumento.Vencido)
            {
                Incidencias =
                [
                    new IncidenciaDocumentalDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL", EstadoDocumento.Vencido, null),
                    new IncidenciaDocumentalDto(Guid.NewGuid(), Guid.NewGuid(), "Aptitud médica", EstadoDocumento.Vencido, null),
                    new IncidenciaDocumentalDto(Guid.NewGuid(), Guid.NewGuid(), "EPI", EstadoDocumento.Urgente, null),
                ],
                DocumentosFaltantes = ["Información Art. 18"],
            }
        ], 1, 1, 1);

        var resultado = await Buscador(new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [trabajador.Id]))
            .Handle(new BuscarGlobalQuery(Dni), CancellationToken.None);

        resultado.Trabajadores.Should().ContainSingle().Which.EstadoTrabajador
            .Should().Be(new EstadoTrabajadorBuscadoDto(EstadoDocumento.Vencido, 2, true));
        _mediador.Enviados.OfType<ObtenerTrabajadoresQuery>().Should().ContainSingle()
            .Which.Should().Match<ObtenerTrabajadoresQuery>(q => q.TrabajadorId == trabajador.Id && q.ConDesgloseDocumental,
                "el estado sale de la consulta del listado, por Id y con su desglose: la misma regla");
    }

    [Fact]
    public async Task Por_dni_un_trabajador_fuera_de_la_cartera_ni_aparece_ni_se_le_calcula_el_estado()
    {
        _trabajadores.ListaTrabajadores.Add(Trabajador.DeEmpresa(_empresaId, "Ana", "Garcia", Dni));

        var resultado = await Buscador(new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: []))
            .Handle(new BuscarGlobalQuery(Dni), CancellationToken.None);

        resultado.Trabajadores.Should().BeEmpty();
        _mediador.Enviados.Should().BeEmpty("fuera de cartera no se pide ningún estado");
    }

    [Fact]
    public async Task Por_nombre_no_se_calcula_estado_y_la_fila_de_mas_solo_decide_ver_mas()
    {
        for (var i = 0; i < 6; i++)
            _trabajadores.ListaTrabajadores.Add(Trabajador.DeEmpresa(_empresaId, "Juan", $"Apellido{i}", OtroDni()));

        var resultado = await Buscador(new AlcanceDatosServiceFalso())
            .Handle(new BuscarGlobalQuery("Juan"), CancellationToken.None);

        resultado.Trabajadores.Should().HaveCount(5).And.OnlyContain(t => t.EstadoTrabajador == null);
        resultado.HayMasTrabajadores.Should().BeTrue();
        resultado.HayMasCentros.Should().BeFalse();
        _mediador.Enviados.Should().BeEmpty("por nombre no hay estado: es N+1 por tecla");
    }

    [Fact]
    public async Task Con_cinco_coincidencias_no_hay_ver_mas()
    {
        for (var i = 0; i < 5; i++)
            _trabajadores.ListaTrabajadores.Add(Trabajador.DeEmpresa(_empresaId, "Juan", $"Apellido{i}", OtroDni()));

        var resultado = await Buscador(new AlcanceDatosServiceFalso())
            .Handle(new BuscarGlobalQuery("Juan"), CancellationToken.None);

        resultado.Trabajadores.Should().HaveCount(5);
        resultado.HayMasTrabajadores.Should().BeFalse();
    }

    private ContarVencenEstaSemanaDeEmpresaQueryHandler Contador(IAlcanceDatosService alcance) =>
        new(_documentos, _trabajadores, _proyectos, alcance);

    [Fact]
    public async Task Vencen_esta_semana_cuenta_de_hoy_al_domingo_la_empresa_y_sus_trabajadores_en_cartera()
    {
        var hoy = DiaDeNegocio.Hoy();
        var domingo = hoy.AddDays((7 - (int)hoy.DayOfWeek) % 7);
        var tipo = Guid.NewGuid();
        var enCartera = Trabajador.DeEmpresa(_empresaId, "Ana", "Garcia", OtroDni());
        var fueraDeCartera = Trabajador.DeEmpresa(_empresaId, "Luis", "Mora", OtroDni());
        var deOtraEmpresa = Trabajador.DeEmpresa(Guid.NewGuid(), "Eva", "Sanz", OtroDni());
        _trabajadores.ListaTrabajadores.AddRange([enCartera, fueraDeCartera, deOtraEmpresa]);

        Documento Vence(bool deEmpresa, Guid propietario, DateOnly fecha) => deEmpresa
            ? Documento.DeEmpresa(propietario, tipo, hoy.AddYears(-1), VigenciaDocumento.VenceEl(fecha))
            : Documento.DeTrabajador(propietario, tipo, hoy.AddYears(-1), VigenciaDocumento.VenceEl(fecha));

        _documentos.ListaDocumentos.AddRange(
        [
            Vence(true, _empresaId, hoy),                  // cuenta
            Vence(false, enCartera.Id, domingo),         // cuenta
            Vence(false, enCartera.Id, hoy.AddDays(-1)), // ya vencido: no «vence»
            Vence(false, enCartera.Id, domingo.AddDays(1)), // la semana que viene
            Vence(false, fueraDeCartera.Id, hoy),        // fuera de cartera
            Vence(false, deOtraEmpresa.Id, hoy),         // otra Empresa
            Documento.DeEmpresa(_empresaId, tipo, hoy.AddYears(-1), VigenciaDocumento.NoCaduca),
        ]);

        var recuento = await Contador(new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, empresaIdsVisibles: [_empresaId], trabajadorIdsVisibles: [enCartera.Id, deOtraEmpresa.Id]))
            .Handle(new ContarVencenEstaSemanaDeEmpresaQuery(_empresaId), CancellationToken.None);

        recuento.Should().Be(2);
    }

    [Fact]
    public async Task Vencen_esta_semana_de_una_empresa_fuera_de_cartera_no_se_calcula()
    {
        _documentos.ListaDocumentos.Add(Documento.DeEmpresa(
            _empresaId, Guid.NewGuid(), DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy())));

        var recuento = await Contador(new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, empresaIdsVisibles: [Guid.NewGuid()], clienteIdsVisibles: [], subcontrataIdsVisibles: []))
            .Handle(new ContarVencenEstaSemanaDeEmpresaQuery(_empresaId), CancellationToken.None);

        recuento.Should().BeNull("null es «no calculado», no un 0 que confirme que la Empresa existe");
    }

    [Fact]
    public async Task Vencen_esta_semana_cuenta_los_trabajadores_de_la_empresa_como_subcontrata()
    {
        var deSubcontrata = Trabajador.DeSubcontrata(_empresaId, "Ana", "Garcia", OtroDni());
        _trabajadores.ListaTrabajadores.Add(deSubcontrata);
        _documentos.ListaDocumentos.Add(VenceHoy(deSubcontrata.Id));

        var recuento = await Contador(new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, subcontrataIdsVisibles: [_empresaId], trabajadorIdsVisibles: [deSubcontrata.Id]))
            .Handle(new ContarVencenEstaSemanaDeEmpresaQuery(_empresaId), CancellationToken.None);

        recuento.Should().Be(1, "el Trabajador de subcontrata de la Empresa es suyo para reclamar");
    }

    [Fact]
    public async Task Vencen_esta_semana_no_cuenta_un_voluntario_inactivo()
    {
        var cuentaAjena = Trabajador.DeEmpresa(_empresaId, "Ana", "Garcia", OtroDni());
        var voluntario = Trabajador.DeEmpresa(_empresaId, "Vic", "Voluntario", OtroDni());
        voluntario.CambiarTipoContratacion(TipoContratacion.Voluntario);
        _trabajadores.ListaTrabajadores.AddRange([cuentaAjena, voluntario]);
        _documentos.ListaDocumentos.AddRange([VenceHoy(cuentaAjena.Id), VenceHoy(voluntario.Id)]);

        var recuento = await Contador(new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, empresaIdsVisibles: [_empresaId], trabajadorIdsVisibles: [cuentaAjena.Id, voluntario.Id]))
            .Handle(new ContarVencenEstaSemanaDeEmpresaQuery(_empresaId), CancellationToken.None);

        recuento.Should().Be(1, "un voluntario sin evento en curso no avisa de sus vencimientos");
    }

    [Fact]
    public async Task Vencen_esta_semana_de_una_empresa_visible_solo_como_subcontrata_si_se_calcula()
    {
        var recuento = await Contador(new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, empresaIdsVisibles: [], clienteIdsVisibles: [], subcontrataIdsVisibles: [_empresaId]))
            .Handle(new ContarVencenEstaSemanaDeEmpresaQuery(_empresaId), CancellationToken.None);

        recuento.Should().Be(0, "visible como Subcontrata está en cartera: se calcula, aunque no haya nada");
    }

    [Fact]
    public async Task Vencen_esta_semana_no_cuenta_documentos_de_empresa_fuera_del_alcance_de_empresa()
    {
        var trabajador = Trabajador.DeEmpresa(_empresaId, "Ana", "Garcia", OtroDni());
        _trabajadores.ListaTrabajadores.Add(trabajador);
        _documentos.ListaDocumentos.AddRange(
        [
            Documento.DeEmpresa(_empresaId, Guid.NewGuid(), DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy())),
            VenceHoy(trabajador.Id),
        ]);

        // Visible como Cliente empresarial y como Subcontrata, pero no en el alcance de Empresa: /documentos no le
        // enseña sus documentos de Empresa, así que el recuento tampoco.
        var recuento = await Contador(new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, empresaIdsVisibles: [], clienteIdsVisibles: [_empresaId],
                subcontrataIdsVisibles: [_empresaId], trabajadorIdsVisibles: [trabajador.Id]))
            .Handle(new ContarVencenEstaSemanaDeEmpresaQuery(_empresaId), CancellationToken.None);

        recuento.Should().Be(1);
    }

    [Fact]
    public async Task Ver_mas_de_empresas_lleva_al_listado_de_la_primera_que_no_cupo()
    {
        for (var i = 0; i < 6; i++)
            _empresas.ListaEmpresas.Add(new Empresa($"Ebro {i}"));

        var resultado = await Buscador(new AlcanceDatosServiceFalso())
            .Handle(new BuscarGlobalQuery("Ebro"), CancellationToken.None);

        resultado.Empresas.Should().HaveCount(5);
        resultado.ListadoVerMasEmpresas.Should().Be("empresas");
        resultado.Empresas.Should().OnlyContain(e => e.UrlDestino.StartsWith("/empresas?q="));
    }

    private Documento VenceHoy(Guid trabajadorId) => Documento.DeTrabajador(
        trabajadorId, Guid.NewGuid(), DiaDeNegocio.Hoy().AddYears(-1), VigenciaDocumento.VenceEl(DiaDeNegocio.Hoy()));

    [Fact]
    public async Task Vencen_esta_semana_de_un_cliente_empresarial_en_cartera_si_se_calcula()
    {
        var recuento = await Contador(new AlcanceDatosServiceFalso(
                tieneAccesoTotal: false, empresaIdsVisibles: [], clienteIdsVisibles: [_empresaId]))
            .Handle(new ContarVencenEstaSemanaDeEmpresaQuery(_empresaId), CancellationToken.None);

        recuento.Should().Be(0);
    }

    private sealed class VehiculosVacios : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new Integraciones.TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }
}
