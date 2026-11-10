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
    private readonly Empresa _empresaDelCentroEntidad = EnTenant(new Empresa("Empresa del Centro"), Tenant);
    private Guid _empresaDelCentro => _empresaDelCentroEntidad.Id;

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
        empresas.ListaEmpresas.AddRange([_titular, _contratistaPrincipal, _empresaDelCentroEntidad]);
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

    /// <summary>
    /// Separa las dos capas: el dominio (<c>Centro.AnclarRelacionDeEntrada</c>) rechaza la arista de otro Tenant con
    /// el mismo error, así que el código del error no demuestra el filtro del comando. Lo demuestra que la cartera
    /// no llega a consultarse: con el filtro de Tenant, la arista ajena no se encuentra y el comando responde antes
    /// de mirar si su X es visible; sin él, la consulta y llega al dominio.
    /// </summary>
    [Fact]
    public async Task Una_relacion_de_otro_Tenant_la_descarta_el_comando_antes_de_mirar_la_cartera()
    {
        var e = Montar(tenantRelacion: Guid.NewGuid());
        var clientesVisibles = new ClientesQueAnotanLaConsulta([_titular.Id, _contratistaPrincipal.Id]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: clientesVisibles,
            centroIdsVisibles: [e.Centro.Id], centroIdsParaGestion: [e.Centro.Id]);

        var resultado = await Ejecutar(e, e.Relacion.Id, alcance);

        resultado.Error.Codigo.Should().Be("Centro.RelacionEntradaNoEncontrada");
        clientesVisibles.Consultada.Should().BeFalse(
            "el comando busca la arista solo en el Tenant propietario del Centro y no llega a la cartera ni al dominio");
        e.Uow.VecesGuardado.Should().Be(0);
    }

    /// <summary>Lista de Clientes empresariales visibles que anota si alguien la ha recorrido.</summary>
    private sealed class ClientesQueAnotanLaConsulta(List<Guid> ids) : IReadOnlyList<Guid>
    {
        public bool Consultada { get; private set; }
        public Guid this[int index] => Anotar()[index];
        public int Count => Anotar().Count;
        public IEnumerator<Guid> GetEnumerator() => Anotar().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        private List<Guid> Anotar()
        {
            Consultada = true;
            return ids;
        }
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
        var comando = new EstablecerRelacionDeEntradaCentroCommand(Guid.Empty, Guid.Empty, Guid.NewGuid());

        new EstablecerRelacionDeEntradaCentroCommandValidator().Validate(comando).Errors
            .Select(x => x.PropertyName).Should()
            .BeEquivalentTo([nameof(comando.CentroId), nameof(comando.RelacionEmpresarialId)]);
    }

    [Fact]
    public void El_validador_rechaza_una_version_vacia_con_o_sin_ancla()
    {
        // Con Guid.Empty el control optimista no compara (ConcurrenciaOptimista.Verificar lo deja pasar): sin esta
        // regla, quitar o cambiar el ancla pisaría un cambio concurrente sin conflicto.
        foreach (var relacionId in new Guid?[] { Guid.NewGuid(), null })
        {
            var comando = new EstablecerRelacionDeEntradaCentroCommand(Guid.NewGuid(), relacionId, Guid.Empty);

            new EstablecerRelacionDeEntradaCentroCommandValidator().Validate(comando).Errors
                .Select(x => x.PropertyName).Should().BeEquivalentTo([nameof(comando.Version)]);
        }
    }

    [Fact]
    public void El_validador_acepta_una_version_informada()
    {
        var comando = new EstablecerRelacionDeEntradaCentroCommand(Guid.NewGuid(), null, Guid.NewGuid());

        new EstablecerRelacionDeEntradaCentroCommandValidator().Validate(comando).IsValid.Should().BeTrue();
    }

    private static CentrosQueryContextFalso Centros(params Centro[] centros)
    {
        var contexto = new CentrosQueryContextFalso();
        contexto.ListaCentros.AddRange(centros);
        return contexto;
    }

    /// <summary>El papel sale de la ficha del Centro, que lee el ancla y su X en la misma consulta.</summary>
    private static async Task<PapelDeEntradaDto?> PapelEnLaFicha(
        Escenario e, Centro centro, IAlcanceDatosService alcance)
    {
        var dto = await new ObtenerCentroPorIdQueryHandler(Centros(centro), e.Empresas, alcance,
            PoliticaNotaInternaPruebas.Con(null)).Handle(new(centro.Id), CancellationToken.None);
        dto.Should().NotBeNull("el Centro es visible para quien mira");
        return dto!.PapelDeEntrada;
    }

    private AlcanceDatosServiceFalso SinCarteraSobreX(Centro centro) =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [_titular.Id],
            centroIdsVisibles: [centro.Id], centroIdsParaGestion: [centro.Id]);

    [Fact]
    public async Task Sin_ancla_el_papel_es_contratista_principal_y_no_se_muestra_nada()
    {
        var e = Montar();

        (await PapelEnLaFicha(e, e.Centro, new AlcanceDatosServiceFalso()))
            .Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
    }

    [Fact]
    public async Task Con_ancla_y_X_visible_el_papel_es_subcontrata_de_X()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);

        (await PapelEnLaFicha(e, e.Centro, new AlcanceDatosServiceFalso()))
            .Should().Be(new PapelDeEntradaDto(true, _contratistaPrincipal.Id, "Contratista Principal SL"));
    }

    /// <summary>
    /// Un Gestor CAE sin Asignación de Cartera sobre X no distingue «hay ancla hacia alguien fuera de mi cartera» de
    /// «no hay ancla»: el papel del Centro anclado es igual, campo a campo, al de un Centro gemelo sin ancla.
    /// </summary>
    [Fact]
    public async Task Con_ancla_y_X_fuera_de_la_cartera_el_papel_es_igual_que_sin_ancla()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);
        var gemeloSinAncla = NuevoCentro();

        var conAncla = await PapelEnLaFicha(e, e.Centro, SinCarteraSobreX(e.Centro));
        var sinAncla = await PapelEnLaFicha(e, gemeloSinAncla, SinCarteraSobreX(gemeloSinAncla));

        conAncla.Should().Be(sinAncla);
        conAncla.Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
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
            (await PapelEnLaFicha(e, e.Centro, new AlcanceDatosServiceFalso()))
                .Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
        }
    }

    [Fact]
    public async Task Al_rol_Cliente_no_se_le_muestra_el_papel()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);

        (await PapelEnLaFicha(e, e.Centro, new AlcanceDatosServiceFalso(ladoDeGestion: false)))
            .Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
    }

    /// <summary>
    /// Quitar el ancla cuya X no ve responde con el mismo error, código y mensaje, que el Centro fuera de alcance de
    /// gestión, y no con uno propio que nombre el ancla. El rechazo frente al éxito sin ancla sigue siendo un oráculo
    /// residual, pendiente de decisión (cabecera del comando); este test fija solo la forma del error.
    /// </summary>
    [Fact]
    public async Task Quitar_un_ancla_cuya_X_no_se_ve_responde_como_un_Centro_fuera_de_alcance()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);
        var fueraDeAlcance = await Ejecutar(e, null, new AlcanceDatosServiceFalso(centroIdsParaGestion: []));

        var resultado = await Ejecutar(e, null, SinCarteraSobreX(e.Centro));

        resultado.Error.Should().Be(fueraDeAlcance.Error);
        resultado.Error.Mensaje.Should().Be(fueraDeAlcance.Error.Mensaje);
        e.Centro.RelacionEmpresarialEntradaId.Should().Be(e.Relacion.Id);
        e.Uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task La_ficha_del_Centro_lleva_el_papel_calculado_con_la_razon_social_de_X()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);

        var dto = await new ObtenerCentroPorIdQueryHandler(Centros(e.Centro), e.Empresas, new AlcanceDatosServiceFalso(),
            PoliticaNotaInternaPruebas.Con(null)).Handle(new(e.Centro.Id), CancellationToken.None);

        dto!.PapelDeEntrada.Should().Be(new PapelDeEntradaDto(true, _contratistaPrincipal.Id, "Contratista Principal SL"));
        dto.EmpresaRazonSocial.Should().Be("Empresa del Centro");
    }

    [Fact]
    public async Task Un_ancla_que_apunta_a_otro_Tenant_no_da_papel()
    {
        var e = Montar();
        e.Centro.AnclarRelacionDeEntrada(e.Relacion);
        typeof(EntidadConTenant).GetProperty(nameof(EntidadConTenant.TenantId))!.SetValue(e.Relacion, Guid.NewGuid());

        (await PapelEnLaFicha(e, e.Centro, new AlcanceDatosServiceFalso()))
            .Should().Be(PapelDeEntradaDto.ContratistaPrincipal);
    }
}
