using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components;
using CaeManager.Web.Features.Documentos.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La primera página de filas viaja del prerenderizado al circuito solo si el circuito arranca con el mismo contexto
/// (usuario, Tenant de origen, perfil, Tenant efectivo, ámbito explícito) y pide la misma consulta, y solo una vez.
/// Cualquier otra cosa lee de la base: nunca se sirve en el circuito lo leído para otro contexto.
/// </summary>
public class EstadoPrerenderizadoTests
{
    private const string Clave = "documentos.listado";

    private static readonly Guid Usuario = Guid.NewGuid();
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Beneficiario = Guid.NewGuid();

    private static readonly ClaveContextoMemo Contexto = new(Usuario, Origen, "GestorCae", Beneficiario, null);

    private static readonly ObtenerDocumentosQuery Consulta = new(
        TrabajadorId: null, Ambito: null, Busqueda: null, Pagina: 1, TamanoPagina: 25,
        Estados: [EstadoDocumento.Vigente], ConRecuentosPorEstado: true, ConDestinosEnCentros: true);

    private static readonly ResultadoPaginado<DocumentoListaDto> Resultado =
        new([
            new DocumentoListaDto(
                Guid.NewGuid(), AmbitoAplicacion.Trabajador, "Ana Pérez", "Formación PRL",
                new DateOnly(2026, 1, 10), new DateOnly(2027, 1, 10), EstadoDocumento.Vigente, "/archivo",
                [new AcreditacionResumenDto(Guid.NewGuid(), "Plataforma CAE", default)], Guid.NewGuid(),
                [new DocumentoEnCentroListaDto(Guid.NewGuid(), "Centro Norte", "Cliente SA", "Plataforma CAE",
                    EstadoDocumento.Vigente, new DateOnly(2026, 11, 1))]),
            new DocumentoListaDto(
                Guid.NewGuid(), AmbitoAplicacion.Empresa, "Empresa contraparte", "Seguro RC",
                new DateOnly(2026, 2, 1), null, EstadoDocumento.Vigente, null, []),
        ], TotalElementos: 40, Pagina: 1, TamanoPagina: 25)
        {
            RecuentosPorEstado = new Dictionary<string, int> { ["Vigente"] = 40 },
        };

    private sealed class Almacen(IDictionary<string, byte[]> estado) : IPersistentComponentStateStore
    {
        public Task<IDictionary<string, byte[]>> GetPersistedStateAsync() => Task.FromResult(estado);
        public Task PersistStateAsync(IReadOnlyDictionary<string, byte[]> state) => Task.CompletedTask;
    }

    private sealed class TenantFijo(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class UsuarioFijo(Guid? usuarioId, Guid? origen, string? perfil) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(usuarioId);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(perfil);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(perfil);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(origen);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>Lo que el prerenderizado deja en la respuesta, con las mismas opciones JSON que Blazor.</summary>
    private static async Task<PersistentComponentState> CircuitoConAsync(object? persistido)
    {
        var estado = new Dictionary<string, byte[]>();
        if (persistido is not null)
            estado[Clave] = JsonSerializer.SerializeToUtf8Bytes(persistido, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var gestor = new ComponentStatePersistenceManager(NullLogger<ComponentStatePersistenceManager>.Instance);
        await gestor.RestoreStateAsync(new Almacen(estado));
        return gestor.State;
    }

    private static EstadoPrerenderizado<PaginaDocumentosPrerenderizada>.Persistido Persistido(
        ClaveContextoMemo contexto, object consulta) =>
        new(contexto, JsonSerializer.Serialize(consulta, consulta.GetType()), PaginaDocumentosPrerenderizada.Desde(Resultado));

    private static async Task<EstadoPrerenderizado<PaginaDocumentosPrerenderizada>> EnElCircuitoAsync(
        object? persistido, Guid? usuario = null, Guid? origen = null, string? perfil = "GestorCae", Guid? tenant = null)
    {
        var estado = new EstadoPrerenderizado<PaginaDocumentosPrerenderizada>(
            await CircuitoConAsync(persistido), Clave, interactivo: true);
        await estado.FijarContextoAsync(
            new UsuarioFijo(usuario ?? Usuario, origen ?? Origen, perfil), new TenantFijo(tenant ?? Beneficiario));
        return estado;
    }

    [Fact]
    public async Task Mismo_contexto_y_misma_consulta_usa_lo_prerenderizado_una_sola_vez()
    {
        var estado = await EnElCircuitoAsync(Persistido(Contexto, Consulta));

        estado.TryTomar(Consulta with { }, out var pagina).Should().BeTrue();
        pagina!.AResultado().Should().BeEquivalentTo(Resultado);
        estado.TryTomar(Consulta, out _).Should().BeFalse("las cargas siguientes leen de la base");
    }

    [Fact]
    public async Task Las_filas_restauradas_conservan_los_destinos_por_Centro_que_el_DTO_no_serializa()
    {
        var estado = await EnElCircuitoAsync(Persistido(Contexto, Consulta));

        estado.TryTomar(Consulta, out var pagina).Should().BeTrue();
        var filas = pagina!.AResultado().Elementos;
        filas[0].DestinosEnCentros.Should().BeEquivalentTo(Resultado.Elementos[0].DestinosEnCentros);
        filas[1].DestinosEnCentros.Should().BeNull();
        pagina.AResultado().RecuentosPorEstado.Should().BeEquivalentTo(Resultado.RecuentosPorEstado);
    }

    public static TheoryData<string> OtrosContextos() => ["usuario", "origen", "perfil", "tenant"];

    [Theory]
    [MemberData(nameof(OtrosContextos))]
    public async Task Otro_contexto_en_el_circuito_no_usa_lo_prerenderizado(string cambio)
    {
        var estado = cambio switch
        {
            "usuario" => await EnElCircuitoAsync(Persistido(Contexto, Consulta), usuario: Guid.NewGuid()),
            "origen" => await EnElCircuitoAsync(Persistido(Contexto, Consulta), origen: Guid.NewGuid()),
            "perfil" => await EnElCircuitoAsync(Persistido(Contexto, Consulta), perfil: "Administrador"),
            _ => await EnElCircuitoAsync(Persistido(Contexto, Consulta), tenant: Guid.NewGuid()),
        };

        estado.TryTomar(Consulta, out _).Should().BeFalse("lo leído para otro contexto nunca se sirve");
    }

    [Fact]
    public async Task Con_ambito_explicito_en_el_circuito_no_usa_lo_prerenderizado_sin_el()
    {
        var circuito = await CircuitoConAsync(Persistido(Contexto, Consulta));
        var estado = new EstadoPrerenderizado<PaginaDocumentosPrerenderizada>(circuito, Clave, interactivo: true);
        using (AmbitoTenantExplicito.Establecer(Guid.NewGuid()))
            await estado.FijarContextoAsync(new UsuarioFijo(Usuario, Origen, "GestorCae"), new TenantFijo(Beneficiario));

        estado.TryTomar(Consulta, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Otra_consulta_no_usa_lo_prerenderizado()
    {
        var estado = await EnElCircuitoAsync(Persistido(Contexto, Consulta));

        estado.TryTomar(Consulta with { Pagina = 2 }, out _).Should().BeFalse();
        estado.TryTomar(Consulta, out _).Should().BeFalse("una consulta distinta descarta lo persistido");
    }

    [Fact]
    public async Task Sin_usuario_en_el_circuito_no_usa_nada()
    {
        var circuito = await CircuitoConAsync(Persistido(Contexto, Consulta));
        var estado = new EstadoPrerenderizado<PaginaDocumentosPrerenderizada>(circuito, Clave, interactivo: true);
        await estado.FijarContextoAsync(new UsuarioFijo(null, Origen, "GestorCae"), new TenantFijo(Beneficiario));

        estado.TryTomar(Consulta, out _).Should().BeFalse();
    }

    [Fact]
    public async Task Sin_nada_persistido_lee_de_la_base()
    {
        var estado = await EnElCircuitoAsync(persistido: null);

        estado.TryTomar(Consulta, out _).Should().BeFalse();
    }
}
