using System.Text.RegularExpressions;
using Bunit;
using CaeManager.Application.Contactos;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Bandeja.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Bloque «Se solicita a» del cajón «Reclamar en lote» abierto con lotes concretos (como lo abre «Reclamar a la Empresa»).
/// Fija lo que hace hoy (cada contacto con su casilla marcada, su correo y su motivo; el bloque de destinatarios extra y en
/// copia; los documentos marcados tal como llegan) y la estructura de la que depende el ajuste al ancho del Drawer: con un
/// correo largo, «Contacto predeterminado» se salía por la derecha. bUnit no calcula la maquetación, así que la parte visual
/// se fija en la hoja de estilos fuente, como <c>VisitasListaFilaCompletaCssTests</c>.
/// </summary>
public class DrawerReclamacionLoteDestinatariosTests : BunitContext
{
    private static readonly Guid EmpresaId = Guid.NewGuid();
    private static readonly Guid TrabajadorId = Guid.NewGuid();
    private static readonly Guid ContactoPredeterminadoId = Guid.NewGuid();
    private static readonly Guid ContactoPorTipoId = Guid.NewGuid();
    private const string CorreoLargo = "rocio.redondo.vidal+prevencion.contratista.valmora@ejemplo-de-correo-largo.invalid";

    public DrawerReclamacionLoteDestinatariosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<IMediator>(_ => new MediadorSinConsultas());
        Services.AddScoped<ToastService>();
    }

    /// <summary>Abierto con lotes concretos el cajón no consulta nada: cualquier petición es un fallo del test.</summary>
    private sealed class MediadorSinConsultas : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => Task.CompletedTask;
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => Task.CompletedTask;
    }

    private static IReadOnlyList<LoteReclamacionAgrupadoDto> Lotes() =>
    [
        new(EmpresaId, "Contratista Valmora S.L.", AmbitoAplicacion.Trabajador, null,
            [
                new(Guid.NewGuid(), TrabajadorId, "Ana Pérez", Guid.NewGuid(), "Reconocimiento médico", new DateOnly(2026, 10, 30), EstadoDocumento.Vencido),
                new(Guid.NewGuid(), TrabajadorId, "Ana Pérez", Guid.NewGuid(), "Formación PRL", new DateOnly(2026, 11, 20), EstadoDocumento.Urgente),
            ],
            null,
            [
                new DestinatarioAgendaDto(ContactoPredeterminadoId, "Rocío Redondo Vidal", CorreoLargo, ["Contacto predeterminado"]),
                new DestinatarioAgendaDto(ContactoPorTipoId, "Pablo Ríos", "prl@ejemplo.invalid", ["Formación PRL"]),
            ]),
    ];

    private IRenderedComponent<DrawerReclamacionLote> Abrir()
    {
        var cut = Render<DrawerReclamacionLote>(p => p
            .Add(x => x.Visible, true)
            .Add(x => x.AmbitosDisponibles, [AmbitoAplicacion.Trabajador, AmbitoAplicacion.Empresa])
            .Add(x => x.LotesIniciales, Lotes())
            .Add(x => x.IncluirPendientesSinFecha, true));
        cut.WaitForAssertion(() => cut.FindAll("fieldset.reclamacion-destinatarios").Should().NotBeEmpty());
        return cut;
    }

    [Fact]
    public void Cada_contacto_lleva_su_casilla_marcada_su_nombre_y_correo_y_debajo_su_motivo()
    {
        var cut = Abrir();

        var fieldset = cut.FindAll("fieldset.reclamacion-destinatarios").First(f => f.QuerySelector("legend")?.TextContent.Trim() == "Se solicita a");
        var contactos = fieldset.Children.Where(e => e.ClassList.Contains("campo-checkbox")).ToList();
        contactos.Should().HaveCount(2);

        // El ajuste al ancho (list-page.css) coloca por posición los hijos directos de la etiqueta: casilla, «Nombre — correo»
        // y motivo. Envolverlos en otro elemento rompería la rejilla sin que ningún test lo viera.
        var predeterminado = contactos[0];
        predeterminado.TagName.Should().Be("LABEL");
        predeterminado.Children.Select(e => e.TagName).Should().Equal("INPUT", "SPAN", "SPAN");
        predeterminado.Children[0].GetAttribute("type").Should().Be("checkbox");
        predeterminado.Children[0].HasAttribute("checked").Should().BeTrue("los contactos que resuelve la agenda salen marcados");
        predeterminado.Children[1].TextContent.Should().Be($"Rocío Redondo Vidal — {CorreoLargo}");
        predeterminado.Children[2].ClassList.Should().Contain("reclamacion-motivo");
        predeterminado.Children[2].TextContent.Should().Be("Contacto predeterminado");

        contactos[1].Children[0].HasAttribute("checked").Should().BeTrue();
        contactos[1].Children[2].TextContent.Should().Be("Formación PRL");
    }

    [Fact]
    public void Ofrece_destinatarios_extra_y_en_copia_ademas_de_los_de_la_agenda()
    {
        var cut = Abrir();

        var extra = cut.Find("fieldset.reclamacion-destinatarios.reclamacion-extra");
        extra.QuerySelectorAll("input").Should().HaveCountGreaterThanOrEqualTo(2, "un campo para destinatarios y otro para copia");
    }

    [Fact]
    public void El_cajon_no_filtra_por_estado_marca_y_cuenta_todo_lo_que_le_dan()
    {
        // Qué se reclama lo decide la consulta que arma los lotes (en «Reclamar a la Empresa», ObtenerReclamacionPorAccesoQuery,
        // que deja fuera lo vigente): el cajón envía lo que recibe.
        var cut = Abrir();

        cut.FindAll(".tabla-lote-reclamacion tbody input[type=checkbox]").Should().HaveCount(2)
            .And.OnlyContain(c => c.HasAttribute("checked"));
        cut.FindAll("button").Should().ContainSingle(b => b.TextContent.Trim() == "Enviar (2)");
    }

    // ---------- Hoja de estilos ----------

    private static string Css()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        dir.Should().NotBeNull("se necesita la raíz del repositorio");
        return File.ReadAllText(Path.Combine(dir!, "src", "CaeManager.Web", "wwwroot", "css", "list-page.css"));
    }

    private static List<string> Reglas(string selector)
    {
        var css = Regex.Replace(Css(), @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
        var cuerpos = new List<string>();
        foreach (Match m in Regex.Matches(css, @"(?<sel>[^{}]+)\{(?<c>[^{}]*)\}"))
        {
            var lista = m.Groups["sel"].Value.Split(',').Select(x => Regex.Replace(x, @"\s+", " ").Trim());
            if (lista.Contains(selector)) cuerpos.Add(m.Groups["c"].Value);
        }
        cuerpos.Should().NotBeEmpty($"debe existir una regla con el selector «{selector}»");
        return cuerpos;
    }

    private static void Declara(string selector, string propiedadYValor, string porque) =>
        Reglas(selector).Should().Contain(c => Regex.IsMatch(c, @"(^|;|\s)" + propiedadYValor), porque);

    [Fact]
    public void El_bloque_no_se_ensancha_por_un_correo_largo()
    {
        Declara(".reclamacion-destinatarios", @"min-width:\s*0\s*;",
            "un fieldset nace con min-inline-size: min-content y un correo sin espacios lo sacaba del Drawer");
    }

    [Fact]
    public void Nombre_y_correo_se_parten_y_el_motivo_baja_debajo_alineado_con_el_nombre()
    {
        Declara(".reclamacion-destinatarios > .campo-checkbox", @"display:\s*grid\s*;", "rejilla: casilla y texto en dos columnas");
        Declara(".reclamacion-destinatarios > .campo-checkbox", @"grid-template-columns:\s*auto\s+minmax\(0,\s*1fr\)\s*;",
            "la columna del texto puede encoger hasta cero en vez de desbordar");
        Declara(".reclamacion-destinatarios > .campo-checkbox > span", @"grid-column:\s*2\s*;",
            "el motivo va en la columna del texto, en su propia línea");
        Declara(".reclamacion-destinatarios > .campo-checkbox > span", @"min-width:\s*0\s*;", "sin suelo de ancho mínimo");
        Declara(".reclamacion-destinatarios > .campo-checkbox > span", @"overflow-wrap:\s*anywhere\s*;",
            "el correo se parte donde haga falta");
    }
}
