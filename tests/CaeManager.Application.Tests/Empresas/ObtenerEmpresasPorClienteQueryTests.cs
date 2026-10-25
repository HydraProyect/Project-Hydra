using CaeManager.Application.EstadoMaterializado;
using CaeManager.Domain.EstadoMaterializado;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresas;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// El filtro y la agrupación «Por Cliente» del listado de Empresas. Los dos tocan la cartera COMERCIAL de la
/// contratista (a qué Clientes presta servicio), así que lo que se ata aquí es que nunca enseñan más que lo que
/// el usuario alcanza: ni una Empresa fuera de su alcance de gestión, ni un Cliente fuera de su alcance de
/// lectura, ni una relación ya cerrada (REC-153).
/// </summary>
public class ObtenerEmpresasPorClienteQueryTests
{
    private sealed class SinDocumentos : ICalculoEstadoDocumentalService
    {
        public Task<IReadOnlyList<EstadoDocumento>> CalcularEstadosDeDocumentosDeVehiculoAsync(Guid vehiculoId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, EstadoDocumento>> CalcularPeorEstadoAsync(
            AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, EstadoDocumento>>(new Dictionary<Guid, EstadoDocumento>());

        public Task<IReadOnlyDictionary<Guid, DesgloseDocumentalDto>> CalcularDesgloseAsync(
            AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class CentrosSinUso : ICalculoEstadoCentroService
    {
        public Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ParDocumentalExigido>> ObtenerParesExigidosAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<ParDocumentalDeEmpresaExigido>> ObtenerParesDeEmpresaExigidosAsync(
            IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class DocumentosSinLecturaDeCalculo : DocumentosQueryContextFalso, IDocumentosQueryContext
    {
        IQueryable<Documento> IDocumentosQueryContext.Documentos =>
            throw new InvalidOperationException("No debe ejecutarse la consulta de documentos del cálculo.");
    }

    private sealed class SoloEstadoLeido(Guid id) : ILectorEstadoMaterializado
    {
        public Task<bool> DisponibleAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public IQueryable<EstadoEntidad> Entidades(AmbitoEstadoEntidad ambito) => throw new NotSupportedException();
        public Task<bool> HayFilasQueNoDecideAsync(AmbitoEstadoEntidad ambito, IQueryable<Guid> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IdsPorEstadoLeido?> LeerEstadoCompletoAsync(AmbitoEstadoEntidad ambito, IQueryable<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IdsPorEstadoLeido?>(new([], [], [], [], [], [], [id]));
        public Task<IReadOnlyDictionary<Guid, EstadoLeido>?> LeerAsync(
            AmbitoEstadoEntidad ambito, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, EstadoLeido>?>(ids.ToDictionary(x => x, _ => new EstadoLeido(EstadoDocumento.Vigente, false, [])));
    }

    private sealed class CalculoProhibido : ICalculoEstadoDocumentalService, IRequisitosObligatoriosIncumplidosService
    {
        public Task<IReadOnlyList<EstadoDocumento>> CalcularEstadosDeDocumentosDeVehiculoAsync(Guid vehiculoId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No debe calcular el estado.");
        public Task<IReadOnlyDictionary<Guid, EstadoDocumento>> CalcularPeorEstadoAsync(
            AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No debe leer documentos para calcular el estado.");
        public Task<IReadOnlyDictionary<Guid, DesgloseDocumentalDto>> CalcularDesgloseAsync(
            AmbitoAplicacion ambito, IReadOnlyList<Guid> propietarioIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No se pidió desglose.");
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>> DeEmpresasAsync(
            IQueryable<Guid> empresaIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No debe calcular los requisitos de Empresa.");
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<RequisitoObligatorioIncumplido>>> DeTrabajadoresAsync(
            IQueryable<Guid> trabajadorIds, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RequisitosObligatoriosIncumplidos> DeEmpresasYTrabajadoresAsync(
            IQueryable<Guid> empresaIds, IQueryable<Guid> trabajadorIds, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No debe calcular los requisitos de Empresa.");
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "Vigente")]
    [InlineData(true, "Vencido")]
    public async Task El_estado_leido_no_consulta_el_calculo_ni_los_umbrales(bool recuentos, string? filtro)
    {
        var contexto = new EmpresasQueryContextFalso();
        var empresa = new Empresa("Empresa propia");
        contexto.ListaEmpresas.Add(empresa);
        var calculo = new CalculoProhibido();
        var handler = new ObtenerEmpresasQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: true), calculo,
            new DocumentosSinLecturaDeCalculo(), new ConfiguracionQueryContextFalso(),
            new AsignacionesQueryContextFalso(), new TrabajadoresQueryContextFalso(), new CentrosSinUso(),
            calculo, new SoloEstadoLeido(empresa.Id));

        // Sin parámetros sembrados: SingleAsync fallaría si se intentaran leer los umbrales del cálculo.
        var resultado = await handler.Handle(new ObtenerEmpresasQuery(null,
            ConRecuentosPorEstado: recuentos, EstadoDocumental: filtro), CancellationToken.None);

        var esperado = filtro == "Vencido" ? 0 : 1;
        resultado.TotalElementos.Should().Be(esperado);
        resultado.Elementos.Should().HaveCount(esperado);
        resultado.Elementos.Select(e => e.EstadoDocumental).Should().Equal(
            Enumerable.Repeat<EstadoDocumento?>(EstadoDocumento.Vigente, esperado));
    }

    private sealed class EstadoLeidoPorClave(IdsPorEstadoLeido ids) : ILectorEstadoMaterializado
    {
        public Task<bool> DisponibleAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public IQueryable<EstadoEntidad> Entidades(AmbitoEstadoEntidad ambito) => throw new NotSupportedException();
        public Task<bool> HayFilasQueNoDecideAsync(AmbitoEstadoEntidad ambito, IQueryable<Guid> ids, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<IdsPorEstadoLeido?> LeerEstadoCompletoAsync(AmbitoEstadoEntidad ambito, IQueryable<Guid> consulta, CancellationToken cancellationToken) =>
            Task.FromResult<IdsPorEstadoLeido?>(ids);
        public Task<IReadOnlyDictionary<Guid, EstadoLeido>?> LeerAsync(
            AmbitoEstadoEntidad ambito, IReadOnlyCollection<Guid> pagina, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<Guid, EstadoLeido>?>(pagina.ToDictionary(x => x, x => new EstadoLeido(
                ids.Vencido.Contains(x) ? EstadoDocumento.Vencido
                    : ids.Proximo.Contains(x) ? EstadoDocumento.Proximo
                    : EstadoDocumento.Vigente, false, [])));
    }

    [Theory]
    [InlineData("Vigente,Proximo", 2)]
    [InlineData("Vencido", 1)]
    [InlineData("Vigente,Vencido,Proximo", 3)]
    [InlineData("EnTolerancia", 0)]
    [InlineData("desconocido", 3)]
    public async Task Con_el_estado_leido_el_total_suma_solo_las_claves_pedidas_de_la_franja(string filtro, int esperado)
    {
        var contexto = new EmpresasQueryContextFalso();
        var vigente = new Empresa("Vigente S.L.");
        var vencida = new Empresa("Vencida S.L.");
        var proxima = new Empresa("Proxima S.L.");
        contexto.ListaEmpresas.AddRange([vigente, vencida, proxima]);
        var calculo = new CalculoProhibido();
        var handler = new ObtenerEmpresasQueryHandler(
            contexto, new AlcanceDatosServiceFalso(tieneAccesoTotal: true), calculo,
            new DocumentosSinLecturaDeCalculo(), new ConfiguracionQueryContextFalso(),
            new AsignacionesQueryContextFalso(), new TrabajadoresQueryContextFalso(), new CentrosSinUso(),
            calculo, new EstadoLeidoPorClave(new([vencida.Id], [], [], [], [proxima.Id], [], [vigente.Id])));

        var resultado = await handler.Handle(new ObtenerEmpresasQuery(null,
            ConRecuentosPorEstado: true, EstadoDocumental: filtro), CancellationToken.None);

        // El total ya no sale de un COUNT: tiene que coincidir con las filas que deja el filtro.
        resultado.TotalElementos.Should().Be(esperado);
        resultado.Elementos.Should().HaveCount(esperado);
        resultado.RecuentosPorEstado!.Values.Sum().Should().Be(3, "la franja cuenta antes del filtro de estado");
    }

    private sealed class Escenario
    {
        public Empresa Aragon { get; } = new("Aragon Montajes S.L.");
        public Empresa Basauri { get; } = new("Basauri Obras S.L.");
        public Empresa Castro { get; } = new("Castro Servicios S.L.");
        public Empresa Dominguez { get; } = new("Dominguez Reformas S.L.");
        public Empresa Umbrella { get; } = Empresa.CrearComoCliente("Umbrella Iberica S.A.", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        public Empresa Cyberdyne { get; } = Empresa.CrearComoCliente("Cyberdyne Iberica S.A.", "B10380202", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        public EmpresasQueryContextFalso Contexto { get; } = new();

        /// <summary>
        /// Aragon sirve a Umbrella y a Cyberdyne; Basauri, a Cyberdyne; Castro, a Umbrella con la relación ya cerrada;
        /// Dominguez no tiene Clientes.
        /// </summary>
        public Escenario()
        {
            foreach (var e in new[] { Aragon, Basauri, Castro, Dominguez, Umbrella, Cyberdyne })
                Contexto.ListaEmpresas.Add(e);

            var ahora = DateTime.UtcNow;
            Contexto.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(Aragon.Id, Umbrella.Id, ahora));
            Contexto.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(Aragon.Id, Cyberdyne.Id, ahora));
            Contexto.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(Basauri.Id, Cyberdyne.Id, ahora));
            var cerrada = RelacionEmpresarial.Crear(Castro.Id, Umbrella.Id, ahora.AddDays(-30));
            cerrada.Cerrar(ahora);
            Contexto.ListaRelacionesEmpresariales.Add(cerrada);
        }

        public ObtenerEmpresasQueryHandler Handler(AlcanceDatosServiceFalso alcance) => new(
            Contexto, alcance, new SinDocumentos(), new DocumentosQueryContextFalso(), new ConfiguracionQueryContextFalso(),
            new AsignacionesQueryContextFalso(), new TrabajadoresQueryContextFalso(), new CentrosSinUso(),
            new Centros.RequisitosObligatoriosSinIncumplidos());
    }

    private static AlcanceDatosServiceFalso Gestor(Escenario e, params Empresa[] empresas) =>
        new(tieneAccesoTotal: false, empresaIdsVisibles: empresas.Select(x => x.Id).ToList(),
            clienteIdsVisibles: [e.Umbrella.Id, e.Cyberdyne.Id]);

    [Fact]
    public async Task El_filtro_por_Cliente_deja_las_Empresas_que_le_prestan_servicio()
    {
        var e = new Escenario();
        var handler = e.Handler(Gestor(e, e.Aragon, e.Basauri, e.Castro, e.Dominguez));

        var resultado = await handler.Handle(new ObtenerEmpresasQuery(null, ClienteEmpresarialId: e.Cyberdyne.Id), CancellationToken.None);

        resultado.Elementos.Select(x => x.Id).Should().BeEquivalentTo([e.Aragon.Id, e.Basauri.Id]);
        resultado.TotalElementos.Should().Be(2);
    }

    [Fact]
    public async Task El_filtro_por_Cliente_nunca_enseña_una_Empresa_fuera_de_la_cartera_de_gestion()
    {
        var e = new Escenario();
        // Basauri sirve a Cyberdyne pero no está en la cartera del usuario.
        var handler = e.Handler(Gestor(e, e.Aragon, e.Dominguez));

        var resultado = await handler.Handle(new ObtenerEmpresasQuery(null, ClienteEmpresarialId: e.Cyberdyne.Id), CancellationToken.None);

        resultado.Elementos.Select(x => x.Id).Should().Equal(e.Aragon.Id);
    }

    [Fact]
    public async Task Un_usuario_de_portal_no_obtiene_Empresas_por_Cliente_aunque_las_lea()
    {
        var e = new Escenario();
        // Las lee (alcance de lectura) pero no las gestiona: la cartera comercial de la contratista no es de portal.
        var portal = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsVisibles: [e.Aragon.Id, e.Basauri.Id], empresaIdsParaGestion: [],
            clienteIdsVisibles: [e.Cyberdyne.Id]);
        var handler = e.Handler(portal);

        var filtrada = await handler.Handle(new ObtenerEmpresasQuery(null, ClienteEmpresarialId: e.Cyberdyne.Id), CancellationToken.None);
        var agrupada = await handler.Handle(
            new ObtenerEmpresasQuery(null, OrdenarPor: nameof(EmpresaListaDto.ClientePrincipal), ConClientePrincipal: true), CancellationToken.None);

        filtrada.Elementos.Should().BeEmpty();
        agrupada.Elementos.Should().NotBeEmpty("sus Empresas se siguen listando");
        agrupada.Elementos.Should().OnlyContain(x => x.ClientePrincipal == null, "no ve a qué Clientes sirven");
    }

    [Fact]
    public async Task Un_Cliente_fuera_del_alcance_de_lectura_no_filtra_ni_agrupa()
    {
        var e = new Escenario();
        var soloUmbrella = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsVisibles: [e.Aragon.Id, e.Basauri.Id], clienteIdsVisibles: [e.Umbrella.Id]);
        var handler = e.Handler(soloUmbrella);

        var filtrada = await handler.Handle(new ObtenerEmpresasQuery(null, ClienteEmpresarialId: e.Cyberdyne.Id), CancellationToken.None);
        var agrupada = await handler.Handle(
            new ObtenerEmpresasQuery(null, OrdenarPor: nameof(EmpresaListaDto.ClientePrincipal), ConClientePrincipal: true), CancellationToken.None);

        filtrada.Elementos.Should().BeEmpty();
        agrupada.Elementos.Single(x => x.Id == e.Aragon.Id).ClientePrincipal.Should().Be(e.Umbrella.RazonSocial);
        agrupada.Elementos.Single(x => x.Id == e.Basauri.Id).ClientePrincipal.Should().BeNull(
            "su único Cliente está fuera del alcance de lectura");
    }

    [Fact]
    public async Task Una_relacion_cerrada_no_cuenta_para_el_filtro()
    {
        var e = new Escenario();
        var handler = e.Handler(Gestor(e, e.Aragon, e.Basauri, e.Castro, e.Dominguez));

        var resultado = await handler.Handle(new ObtenerEmpresasQuery(null, ClienteEmpresarialId: e.Umbrella.Id), CancellationToken.None);

        resultado.Elementos.Select(x => x.Id).Should().Equal(e.Aragon.Id);
    }

    [Fact]
    public async Task Agrupar_por_Cliente_ordena_por_el_primer_Cliente_y_deja_las_Empresas_sin_Cliente_al_final()
    {
        var e = new Escenario();
        var handler = e.Handler(Gestor(e, e.Aragon, e.Basauri, e.Castro, e.Dominguez));

        var resultado = await handler.Handle(
            new ObtenerEmpresasQuery(null, OrdenarPor: nameof(EmpresaListaDto.ClientePrincipal), ConClientePrincipal: true),
            CancellationToken.None);

        // Aragon sirve a dos Clientes y sale una sola vez, bajo el primero por razón social (Cyberdyne).
        resultado.Elementos.Select(x => (x.RazonSocial, x.ClientePrincipal)).Should().Equal(
            (e.Aragon.RazonSocial, e.Cyberdyne.RazonSocial),
            (e.Basauri.RazonSocial, e.Cyberdyne.RazonSocial),
            (e.Castro.RazonSocial, null),
            (e.Dominguez.RazonSocial, null));
    }

    [Fact]
    public async Task Sin_pedirlo_la_lista_no_lleva_Cliente()
    {
        var e = new Escenario();
        var handler = e.Handler(Gestor(e, e.Aragon, e.Basauri));

        var resultado = await handler.Handle(new ObtenerEmpresasQuery(null), CancellationToken.None);

        resultado.Elementos.Should().HaveCount(2).And.OnlyContain(x => x.ClientePrincipal == null);
    }
}
