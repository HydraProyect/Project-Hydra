using CaeManager.Application.Centros.Commands.EstablecerRelacionDeEntradaCentro;
using CaeManager.Application.Centros.Queries;
using CaeManager.Application.Centros.Queries.ObtenerCentroPorId;
using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Papel por Centro (DECISION_LOG, 2026-11-08): el comando de la Ficha del Centro que fija el ancla y el
/// cálculo del papel que la lee. La arista tiene que ser del mismo Tenant propietario y su cliente X, visible
/// para quien la fija; X solo se nombra a quien lo ve.
/// </summary>
public class PapelDeEntradaEnCentroTests
{
    private static readonly Guid Tenant = Guid.NewGuid();
    private readonly Empresa _titular = EnTenant(Empresa.CrearComoCliente("Titular SA", "B12345674", false, null, null), Tenant);
    private readonly Empresa _contratistaPrincipal = EnTenant(Empresa.CrearComoCliente("Contratista Principal SL", "B76543214", false, null, null), Tenant);
    private readonly Guid _empresaDelCentro = Guid.NewGuid();

    private static T EnTenant<T>(T entidad, Guid tenant) where T : EntidadConTenant
    {
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(entidad, tenant);
        return entidad;
    }

    private Centro NuevoCentro() => EnTenant(new Centro(_titular.Id, _empresaDelCentro, "Planta Zaragoza"), Tenant);

    private RelacionEmpresarial RelacionConX(Guid? tenant = null) =>
        EnTenant(RelacionEmpresarial.Crear(_empresaDelCentro, _contratistaPrincipal.Id, DateTime.UtcNow), tenant ?? Tenant);

    private sealed record Escenario(
        Centro Centro, RelacionEmpresarial Relacion, CentroRepositorioFalso Repositorio,
        EmpresasQueryContextFalso Empresas, UnitOfWorkFalso Uow);

    private Escenario Montar(Guid? tenantRelacion = null)
    {
        var centro = NuevoCentro();
        var relacion = RelacionConX(tenantRelacion);
        var repositorio = new CentroRepositorioFalso();
        repositorio.Agregar(centro);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.AddRange([_titular, _contratistaPrincipal]);
        empresas.ListaRelacionesEmpresariales.Add(relacion);
        return new Escenario(centro, relacion, repositorio, empresas, new UnitOfWorkFalso());
    }

    private static Task<Result> Ejecutar(Escenario e, Guid? relacionId, IAlcanceDatosService alcance) =>
        new EstablecerRelacionDeEntradaCentroCommandHandler(e.Repositorio, e.Empresas, alcance, e.Uow)
            .Handle(new EstablecerRelacionDeEntradaCentroCommand(e.Centro.Id, relacionId, e.Centro.Version),
                CancellationToken.None);

    [Fact]
    public async Task Con_gestion_y_X_visible_fija_el_ancla_y_guarda()
    {
        var e = Montar();

        var resultado = await Ejecutar(e, e.Relacion.Id, new AlcanceDatosServiceFalso());

        resultado.EsExitoso.Should().BeTrue();
        e.Centro.RelacionEmpresarialEntradaId.Should().Be(e.Relacion.Id);
        e.Uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_gestion_sobre_el_Centro_responde_como_inexistente()
    {
        var e = Montar();

        var resultado = await Ejecutar(e, e.Relacion.Id, new AlcanceDatosServiceFalso(centroIdsParaGestion: []));

        resultado.Error.Codigo.Should().Be("Centro.NoEncontrado");
        e.Centro.RelacionEmpresarialEntradaId.Should().BeNull();
        e.Uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_relacion_de_otro_Tenant_se_rechaza_como_inexistente()
    {
        var e = Montar(tenantRelacion: Guid.NewGuid());

        var resultado = await Ejecutar(e, e.Relacion.Id, new AlcanceDatosServiceFalso());

        resultado.Error.Codigo.Should().Be("Centro.RelacionEntradaNoEncontrada");
        e.Centro.RelacionEmpresarialEntradaId.Should().BeNull();
        e.Uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Con_X_fuera_de_los_Clientes_visibles_se_rechaza_igual_que_inexistente()
    {
        var e = Montar();
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_titular.Id],
            centroIdsVisibles: [e.Centro.Id], centroIdsParaGestion: [e.Centro.Id]);

        var resultado = await Ejecutar(e, e.Relacion.Id, alcance);

        resultado.Error.Codigo.Should().Be("Centro.RelacionEntradaNoEncontrada");
        e.Centro.RelacionEmpresarialEntradaId.Should().BeNull();
        e.Uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_relacion_quita_el_ancla()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);

        var resultado = await Ejecutar(e, null, new AlcanceDatosServiceFalso());

        resultado.EsExitoso.Should().BeTrue();
        e.Centro.RelacionEmpresarialEntradaId.Should().BeNull();
        e.Uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Una_version_antigua_no_guarda()
    {
        var e = Montar();
        var handler = new EstablecerRelacionDeEntradaCentroCommandHandler(
            e.Repositorio, e.Empresas, new AlcanceDatosServiceFalso(), e.Uow);

        var resultado = await handler.Handle(
            new EstablecerRelacionDeEntradaCentroCommand(e.Centro.Id, e.Relacion.Id, Guid.NewGuid()),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        e.Uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_rechaza_el_Centro_vacio_y_la_relacion_vacia()
    {
        var comando = new EstablecerRelacionDeEntradaCentroCommand(Guid.Empty, Guid.Empty);

        new EstablecerRelacionDeEntradaCentroCommandValidator().Validate(comando).Errors
            .Select(x => x.PropertyName).Should()
            .BeEquivalentTo([nameof(comando.CentroId), nameof(comando.RelacionEmpresarialId)]);
    }

    private static CentrosQueryContextFalso Centros(Centro centro)
    {
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        return centros;
    }

    [Fact]
    public async Task Sin_ancla_el_papel_es_contratista_principal_y_no_se_muestra_nada()
    {
        var e = Montar();

        var papel = await PapelDeEntradaEnCentro.CalcularAsync(
            e.Centro.Id, Centros(e.Centro), e.Empresas, new AlcanceDatosServiceFalso(), CancellationToken.None);

        papel.Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
    }

    [Fact]
    public async Task Con_ancla_y_X_visible_el_papel_es_subcontrata_de_X()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);

        var papel = await PapelDeEntradaEnCentro.CalcularAsync(
            e.Centro.Id, Centros(e.Centro), e.Empresas, new AlcanceDatosServiceFalso(), CancellationToken.None);

        papel.Should().Be(new PapelDeEntradaDto(true, _contratistaPrincipal.Id, "Contratista Principal SL"));
    }

    [Fact]
    public async Task Con_ancla_y_X_no_visible_el_papel_es_subcontrata_sin_nombrar_a_X()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_titular.Id]);

        var papel = await PapelDeEntradaEnCentro.CalcularAsync(
            e.Centro.Id, Centros(e.Centro), e.Empresas, alcance, CancellationToken.None);

        papel.Should().Be(new PapelDeEntradaDto(true, null, null));
    }

    private static void ForzarAncla(Centro centro, RelacionEmpresarial relacion) =>
        typeof(Centro).GetProperty(nameof(Centro.RelacionEmpresarialEntradaId))!.SetValue(centro, relacion.Id);

    [Fact]
    public async Task Un_ancla_heredada_hacia_el_titular_o_de_otra_Empresa_no_da_papel()
    {
        var e = Montar();
        var haciaElTitular = EnTenant(RelacionEmpresarial.Crear(_empresaDelCentro, _titular.Id, DateTime.UtcNow), Tenant);
        var deOtraEmpresa = EnTenant(RelacionEmpresarial.Crear(Guid.NewGuid(), _contratistaPrincipal.Id, DateTime.UtcNow), Tenant);
        e.Empresas.ListaRelacionesEmpresariales.AddRange([haciaElTitular, deOtraEmpresa]);

        foreach (var relacion in new[] { haciaElTitular, deOtraEmpresa })
        {
            ForzarAncla(e.Centro, relacion);
            (await PapelDeEntradaEnCentro.CalcularAsync(
                    e.Centro.Id, Centros(e.Centro), e.Empresas, new AlcanceDatosServiceFalso(), CancellationToken.None))
                .Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
        }
    }

    [Fact]
    public async Task Al_rol_Cliente_no_se_le_muestra_el_papel()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);

        var papel = await PapelDeEntradaEnCentro.CalcularAsync(
            e.Centro.Id, Centros(e.Centro), e.Empresas, new AlcanceDatosServiceFalso(ladoDeGestion: false),
            CancellationToken.None);

        papel.Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
    }

    [Fact]
    public async Task Quitar_un_ancla_cuya_X_no_se_ve_se_rechaza()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [_titular.Id],
            centroIdsVisibles: [e.Centro.Id], centroIdsParaGestion: [e.Centro.Id]);

        var resultado = await Ejecutar(e, null, alcance);

        resultado.Error.Codigo.Should().Be("Centro.RelacionEntradaFueraDeCartera");
        e.Centro.RelacionEmpresarialEntradaId.Should().Be(e.Relacion.Id);
        e.Uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task La_ficha_del_Centro_lleva_el_papel_calculado()
    {
        var e = Montar();
        var empresaDelCentro = EnTenant(new Empresa("Empresa del Centro"), Tenant);
        var centro = EnTenant(new Centro(_titular.Id, empresaDelCentro.Id, "Planta Huesca"), Tenant);
        var relacion = EnTenant(RelacionEmpresarial.Crear(empresaDelCentro.Id, _contratistaPrincipal.Id, DateTime.UtcNow), Tenant);
        centro.AnclarRelacionDeEntrada(relacion);
        e.Empresas.ListaEmpresas.Add(empresaDelCentro);
        e.Empresas.ListaRelacionesEmpresariales.Add(relacion);

        var dto = await new ObtenerCentroPorIdQueryHandler(Centros(centro), e.Empresas, new AlcanceDatosServiceFalso(),
            PoliticaNotaInternaPruebas.Con(null)).Handle(new(centro.Id), CancellationToken.None);

        dto!.PapelDeEntrada.Should().Be(new PapelDeEntradaDto(true, _contratistaPrincipal.Id, "Contratista Principal SL"));
    }

    [Fact]
    public async Task Un_ancla_que_apunta_a_otro_Tenant_no_da_papel()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(e.Relacion, Guid.NewGuid());

        var papel = await PapelDeEntradaEnCentro.CalcularAsync(
            e.Centro.Id, Centros(e.Centro), e.Empresas, new AlcanceDatosServiceFalso(), CancellationToken.None);

        papel.Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
    }
}
