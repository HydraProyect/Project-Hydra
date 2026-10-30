using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Clientes;
using CaeManager.Web.Features.Empresas;
using CaeManager.Web.Features.Subcontratas;
using CaeManager.Web.Features.Subcontratas.Recursos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Empresas, Subcontratas, Clientes y Centros resuelven en su página el estado 4a
/// («Selecciona una empresa de tu cartera») con <c>ContextoEmpresaActiva.ResolverAsync</c> y no enseñan
/// los datos del Tenant de origen. Su exportación aplica la misma condición
/// (<see cref="ClientesAutorizados.PideElegirEmpresa"/>): sin empresa elegida redirige a su página sin
/// consultar el listado ni generar el archivo; con ella, o sin cartera, exporta como antes.
///
/// El registro de exportación se comprueba directamente junto a la guarda. Los roles y el enlace de
/// la cadena de consulta se comprueban en los tests del endpoint, de arquitectura y E2E.
/// </summary>
public class ExportacionesDeListadosEstado4aTests
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Externo = Guid.NewGuid();

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private sealed class RegistroFalso(bool falla = false) : IRegistroExportacionService
    {
        public List<(string Tipo, int Filas, IReadOnlyDictionary<string, string> Criterios)> Registros { get; } = [];

        public Task RegistrarAsync(string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
            CancellationToken cancellationToken = default)
        {
            if (falla) throw new InvalidOperationException("sin rastro");
            Registros.Add((entidadTipo, filas, criterios));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Responde la lista autorizada y, a cualquier consulta paginada del listado, una página vacía.
    /// Anota todo lo que recibe: así se ve que en el estado 4a no sale nada más que la lista autorizada.
    /// </summary>
    private sealed class MediatorFalso(IReadOnlyList<ClienteAutorizadoDto> autorizados) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public IEnumerable<object> ConsultasDelListado => Enviados.Where(e => e is not ObtenerClientesAutorizadosQuery);

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            if (request is ObtenerClientesAutorizadosQuery)
                return Task.FromResult((TResponse)autorizados);

            var tipo = typeof(TResponse);
            if (!tipo.IsGenericType || tipo.GetGenericTypeDefinition() != typeof(ResultadoPaginado<>))
                throw new NotSupportedException(request.GetType().Name);

            var vacia = Array.CreateInstance(tipo.GetGenericArguments()[0], 0);
            return Task.FromResult((TResponse)Activator.CreateInstance(tipo, vacia, 0, 1, 1)!);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private static readonly IStringLocalizer<TextosSubcontratas> Textos =
        new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider()
            .GetRequiredService<IStringLocalizer<TextosSubcontratas>>();

    /// <summary>Cada listado con su página y el nombre del archivo que entrega.</summary>
    public static TheoryData<string, string, string> Listados => new()
    {
        { "Empresas", "/empresas", "empresas.xlsx" },
        { "Subcontratas", "/subcontratas", "subcontratas.xlsx" },
        { "Clientes", "/clientes", "clientes.xlsx" },
        { "Centros", "/centros", "centros.xlsx" },
    };

    private static Task<IResult> ExportarAsync(string listado, IMediator mediador, Guid tenantActual, IRegistroExportacionService registro)
    {
        var tenant = new TenantActualFalso(tenantActual);
        return listado switch
        {
            "Empresas" => EmpresasEndpoints.ExportarAsync(mediador, tenant, registro, default),
            "Subcontratas" => SubcontratasEndpoints.ExportarAsync(mediador, tenant, registro, Textos, default),
            "Clientes" => ClientesEndpoints.ExportarAsync(mediador, tenant, registro, default),
            "Centros" => CentrosEndpoints.ExportarAsync(mediador, tenant, registro, default),
            _ => throw new ArgumentOutOfRangeException(nameof(listado), listado, null),
        };
    }

    private static ClienteAutorizadoDto Propio(bool gestionado = false) =>
        new(Origen, "Operador CAE", EsOrigen: true, EsGestionadoPorOperacion: gestionado);

    private static ClienteAutorizadoDto Cartera() =>
        new(Externo, "Tenant beneficiario", EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true);

    [Theory]
    [MemberData(nameof(Listados))]
    public async Task En_el_estado_4a_redirige_a_la_pagina_y_no_consulta_el_listado_del_origen(string listado, string pagina, string archivo)
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);

        var registro = new RegistroFalso();
        var resultado = await ExportarAsync(listado, mediador, Origen, registro);

        resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be(pagina);
        registro.Registros.Should().BeEmpty();
        mediador.ConsultasDelListado.Should().BeEmpty($"no se piden los datos de la organización de origen ni sale {archivo}");
        mediador.Enviados.Should().ContainSingle("control positivo: la guarda sí consultó la lista autorizada")
            .Which.Should().BeOfType<ObtenerClientesAutorizadosQuery>();
    }

    [Theory]
    [MemberData(nameof(Listados))]
    public async Task Con_una_empresa_de_la_cartera_elegida_exporta(string listado, string pagina, string archivo)
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);

        var registro = new RegistroFalso();
        var resultado = await ExportarAsync(listado, mediador, Externo, registro);

        resultado.Should().NotBeOfType<RedirectHttpResult>($"con empresa elegida no se vuelve a {pagina}");
        resultado.Should().BeOfType<FileStreamHttpResult>().Which.FileDownloadName.Should().Be(archivo);
        var entrada = registro.Registros.Should().ContainSingle().Subject;
        entrada.Tipo.Should().Be(listado == "Centros" ? "Centro" : "Empresa");
        entrada.Filas.Should().Be(0);
        entrada.Criterios.Should().BeEquivalentTo(new Dictionary<string, string> { ["listado"] = listado.ToLowerInvariant() });
        mediador.ConsultasDelListado.Should().NotBeEmpty("el listado de la empresa elegida sí se consulta");
    }

    [Theory]
    [MemberData(nameof(Listados))]
    public async Task Con_el_origen_gestionado_o_sin_cartera_exporta_el_origen(string listado, string pagina, string archivo)
    {
        foreach (var autorizados in new[]
                 {
                     new[] { Propio(gestionado: true), Cartera() },
                     [Propio()],
                 })
        {
            var mediador = new MediatorFalso(autorizados);

            var resultado = await ExportarAsync(listado, mediador, Origen, new RegistroFalso());

            resultado.Should().NotBeOfType<RedirectHttpResult>($"sin estado 4a no se vuelve a {pagina}");
            resultado.Should().BeOfType<FileStreamHttpResult>().Which.FileDownloadName.Should().Be(archivo);
            mediador.ConsultasDelListado.Should().NotBeEmpty();
        }
    }

    [Theory]
    [MemberData(nameof(Listados))]
    public async Task Si_falla_el_registro_no_se_entrega_el_archivo(string listado, string pagina, string archivo)
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);
        var exportar = () => ExportarAsync(listado, mediador, Externo, new RegistroFalso(falla: true));

        await exportar.Should().ThrowAsync<InvalidOperationException>(
            $"sin rastro no se entrega {archivo}, igual que en Documentos").WithMessage("sin rastro");
        mediador.ConsultasDelListado.Should().NotBeEmpty($"el fallo llega tras generar el libro, no en la guarda de {pagina}");
    }

    [Theory]
    [MemberData(nameof(Listados))]
    public async Task Registra_criterios_sin_texto_libre(string listado, string pagina, string archivo)
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);
        var tenant = new TenantActualFalso(Externo);
        var registro = new RegistroFalso();
        var id = Guid.NewGuid();
        var resultado = listado switch
        {
            "Empresas" => await EmpresasEndpoints.ExportarAsync(mediador, tenant, registro, default, q: "01234567Z", estado: "Vencido,Proximo", cliente: id.ToString()),
            "Subcontratas" => await SubcontratasEndpoints.ExportarAsync(mediador, tenant, registro, Textos, default, q: "01234567Z", estado: "Vencido,Proximo", contratadapor: id.ToString()),
            "Clientes" => await ClientesEndpoints.ExportarAsync(mediador, tenant, registro, default, q: "01234567Z", ejecutivo: id.ToString(), orden: "Nombre; 01234567Z"),
            "Centros" => await CentrosEndpoints.ExportarAsync(mediador, tenant, registro, default, q: "01234567Z", empresa: id.ToString(), orden: "Nombre; 01234567Z"),
            _ => throw new ArgumentOutOfRangeException(nameof(listado))
        };

        resultado.Should().BeOfType<FileStreamHttpResult>().Which.FileDownloadName.Should().Be(archivo);
        var criterios = registro.Registros.Should().ContainSingle().Subject.Criterios;
        criterios["listado"].Should().Be(listado.ToLowerInvariant());
        criterios["busqueda"].Should().Be("true");
        criterios[listado switch { "Empresas" => "cliente", "Subcontratas" => "contratadapor", "Clientes" => "ejecutivo", _ => "empresa" }].Should().Be(id.ToString());
        if (listado is "Empresas" or "Subcontratas") criterios["estado"].Should().Be("Vencido,Proximo");
        criterios.Should().NotContainKey("orden", $"el orden de {pagina} no tiene forma de nombre");
        criterios.Values.Should().NotContain(v => v.Contains("01234567Z"));
    }

    [Theory]
    [InlineData("Empresas")]
    [InlineData("Subcontratas")]
    public async Task Un_estado_con_texto_libre_no_llega_al_rastro(string listado)
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);
        var tenant = new TenantActualFalso(Externo);
        var registro = new RegistroFalso();

        if (listado == "Empresas")
            await EmpresasEndpoints.ExportarAsync(mediador, tenant, registro, default, estado: "Vencido,01234567Z,Garcia,4");
        else
            await SubcontratasEndpoints.ExportarAsync(mediador, tenant, registro, Textos, default, estado: "Vencido,01234567Z,Garcia,4", nivel: "12345678");

        registro.Registros.Should().ContainSingle().Which.Criterios.Should().BeEquivalentTo(
            new Dictionary<string, string> { ["listado"] = listado.ToLowerInvariant(), ["estado"] = "Vencido" },
            "solo consta el estado que la consulta reconoce: ni el texto libre, aunque sean solo letras, ni un número");
    }

    [Theory]
    [InlineData("Clientes")]
    [InlineData("Centros")]
    public async Task Los_estados_de_la_franja_solo_llegan_al_rastro_si_son_nombres_validos(string listado)
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);
        var tenant = new TenantActualFalso(Externo);
        var registro = new RegistroFalso();
        var estado = listado == "Clientes" ? "Vencido,01234567Z" : "Vigente,01234567Z";

        if (listado == "Clientes")
            await ClientesEndpoints.ExportarAsync(mediador, tenant, registro, default, estado: estado, orden: "Nombre", desc: true, critico: true);
        else
            await CentrosEndpoints.ExportarAsync(mediador, tenant, registro, default, estado: estado, orden: "Nombre", desc: true);

        var criterios = registro.Registros.Should().ContainSingle().Subject.Criterios;
        criterios.Values.Should().NotContain(v => v.Contains("01234567Z"));
        criterios["estado"].Should().Be(listado == "Clientes" ? "Vencido" : "Vigente");
        criterios["orden"].Should().Be("Nombre");
        criterios["desc"].Should().Be("true");
        if (listado == "Clientes") criterios["critico"].Should().Be("true");
    }

    [Fact]
    public async Task Una_empresa_con_filtro_de_cliente_invalido_no_registra_exportacion()
    {
        var registro = new RegistroFalso();
        var resultado = await EmpresasEndpoints.ExportarAsync(new MediatorFalso([Propio(), Cartera()]),
            new TenantActualFalso(Externo), registro, default, cliente: "invalido");

        resultado.Should().BeOfType<BadRequest>();
        registro.Registros.Should().BeEmpty();
    }
}