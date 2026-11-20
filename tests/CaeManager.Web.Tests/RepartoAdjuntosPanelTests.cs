using Bunit;
using CaeManager.Application.Comunicaciones.Commands.AsignarAdjuntosLote;
using CaeManager.Application.Comunicaciones.Commands.FijarDestinoAdjunto;
using CaeManager.Application.Comunicaciones.Commands.ResolverAdjunto;
using CaeManager.Application.Comunicaciones.Queries.ObtenerRepartoAdjuntos;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El reparto de adjuntos de una conversación (maqueta <c>Adjuntos.dc.html</c>): estado y destino de cada adjunto, fijar el destino,
/// rechazar, descartar, deshacer y asignar por lote. La pantalla no decide quién puede escribir (lo hace el servidor en cada
/// comando): fija aquí que no ofrece lo que el rol no puede hacer y que sus rótulos son los de la maqueta.
/// </summary>
public class RepartoAdjuntosPanelTests : BunitContext
{
    private static readonly Guid Conversacion = Guid.NewGuid();
    private static readonly Guid Marta = Guid.NewGuid();
    private static readonly Guid Pedro = Guid.NewGuid();
    private static readonly Guid EmpresaPropia = Guid.NewGuid();
    private static readonly Guid TipoDniTrabajador = Guid.NewGuid();
    private static readonly Guid TipoSeguroEmpresa = Guid.NewGuid();

    private sealed class Mediador : IMediator
    {
        public List<AdjuntoRepartoDto> Reparto { get; set; } = [];
        public List<object> Enviados { get; } = [];
        public Func<AsignarAdjuntosLoteCommand, Result<IReadOnlyList<ResultadoAsignacionAdjunto>>>? Lote { get; set; }
        public bool RepartoFalla { get; set; }
        /// <summary>Las consultas de selector (Trabajadores, Empresas o tipos) que lanzan al leerse.</summary>
        public HashSet<Type> SelectoresFallan { get; } = [];
        /// <summary>Si se da, la próxima lectura del selector de Trabajadores no responde hasta que el test complete esta tarea.</summary>
        public TaskCompletionSource<IReadOnlyList<TrabajadorSelectorDto>>? RetenerTrabajadores { get; set; }
        public List<TrabajadorSelectorDto> Trabajadores { get; set; } = [new(Marta, "Marta Ruiz", null, null), new(Pedro, "Pedro Gil", null, null)];

        public IEnumerable<T> De<T>() => Enviados.OfType<T>();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            if (SelectoresFallan.Contains(request.GetType())) throw new InvalidOperationException("falla el selector");
            if (request is ObtenerTrabajadoresParaSelectorQuery && RetenerTrabajadores is { } retenida)
            {
                // Solo la primera lectura queda en vuelo; las siguientes responden en el acto.
                RetenerTrabajadores = null;
                return (Task<TResponse>)(object)retenida.Task;
            }
            object? respuesta = request switch
            {
                ObtenerRepartoAdjuntosQuery => RepartoFalla ? throw new InvalidOperationException("falla") : Reparto.ToList(),
                ObtenerTrabajadoresParaSelectorQuery => Trabajadores.ToList(),
                ObtenerEmpresasParaSelectorQuery => new List<EmpresaSelectorDto> { new(EmpresaPropia, "Obras Norte") },
                ObtenerTiposDocumentoQuery =>
                    new List<TipoDocumentoListaDto>
                    {
                        new(TipoDniTrabajador, "DNI", null, false, 1, AmbitoAplicacion.Trabajador, default, default,
                            null, null, null, null, false, false, false, default, []),
                        new(TipoSeguroEmpresa, "Seguro de responsabilidad civil", 12, true, 2, AmbitoAplicacion.Empresa, default, default,
                            null, null, null, null, false, false, false, default, [])
                    },
                FijarDestinoAdjuntoCommand c => Fijar(c),
                ResolverAdjuntoCommand c => Resolver(c),
                AsignarAdjuntosLoteCommand c => Lote is not null ? Lote(c) : Todos(c, EstadoAsignacionAdjunto.Asignado),
                _ => throw new NotSupportedException(request.GetType().Name)
            };
            return Task.FromResult((TResponse)respuesta!);
        }

        private Result<Guid> Fijar(FijarDestinoAdjuntoCommand c)
        {
            var i = Reparto.FindIndex(a => a.Id == c.AdjuntoId);
            Reparto[i] = Reparto[i] with { DestinoTrabajadorId = c.TrabajadorId, DestinoEmpresaId = c.EmpresaId, TipoDocumentoId = c.TipoDocumentoId };
            return Result.Exito(c.AdjuntoId);
        }

        private Result<Guid> Resolver(ResolverAdjuntoCommand c)
        {
            var i = Reparto.FindIndex(a => a.Id == c.AdjuntoId);
            Reparto[i] = c.Resolucion switch
            {
                ResolucionAdjunto.Rechazar => Reparto[i] with { Estado = EstadoRepartoAdjunto.Rechazado, MotivoRechazo = c.Motivo },
                ResolucionAdjunto.Descartar => Reparto[i] with { Estado = EstadoRepartoAdjunto.Descartado },
                _ => Reparto[i] with { Estado = EstadoRepartoAdjunto.Pendiente, MotivoRechazo = null }
            };
            return Result.Exito(c.AdjuntoId);
        }

        public Result<IReadOnlyList<ResultadoAsignacionAdjunto>> Todos(AsignarAdjuntosLoteCommand c, EstadoAsignacionAdjunto estado)
        {
            foreach (var a in c.Adjuntos)
            {
                var i = Reparto.FindIndex(x => x.Id == a.AdjuntoId);
                if (estado == EstadoAsignacionAdjunto.Asignado) Reparto[i] = Reparto[i] with { Estado = EstadoRepartoAdjunto.Asignado };
            }

            IReadOnlyList<ResultadoAsignacionAdjunto> resultados =
                c.Adjuntos.Select(a => new ResultadoAsignacionAdjunto(a.AdjuntoId, estado, null, null)).ToList();
            return Result.Exito(resultados);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private Mediador Preparar(string rol, params AdjuntoRepartoDto[] reparto)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura(rol);
        Services.AddLocalization();
        var mediador = new Mediador { Reparto = reparto.ToList() };
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddSingleton<ILogger<RepartoAdjuntosPanel>>(_ => NullLogger<RepartoAdjuntosPanel>.Instance);
        return mediador;
    }

    private IRenderedComponent<RepartoAdjuntosPanel> Pintar(Action? alCambiar = null) =>
        Render<RepartoAdjuntosPanel>(p => p
            .Add(x => x.ConversacionId, Conversacion)
            .Add(x => x.Recarga, new object())
            .Add(x => x.OnCambiado, EventCallback.Factory.Create(this, () => alCambiar?.Invoke())));

    private static AdjuntoRepartoDto Adj(string nombre, Guid? trabajador = null, Guid? empresa = null, Guid? tipo = null,
        EstadoRepartoAdjunto estado = EstadoRepartoAdjunto.Pendiente, string? motivo = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), nombre, "application/pdf", 1234, estado, trabajador, empresa, tipo, motivo, null, null, null);

    private static string Texto(IRenderedComponent<RepartoAdjuntosPanel> cut) => cut.Find(".adjb").TextContent;

    private static IEnumerable<string> Botones(IRenderedComponent<RepartoAdjuntosPanel> cut) =>
        cut.FindAll(".adjb button").Select(b => b.TextContent.Trim());

    private static void EscribirEmision(IRenderedComponent<RepartoAdjuntosPanel> cut, string nombre, string fecha) =>
        cut.Find($"input[aria-label='Fecha de emisión de {nombre}']").Change(fecha);

    // ---------- Rótulos y estados de la maqueta ----------

    [Fact]
    public void Muestra_cabecera_estados_y_destino_de_cada_adjunto_con_los_rotulos_de_la_maqueta()
    {
        Preparar(Roles.GestorCae,
            Adj("dni-marta.pdf", trabajador: Marta, tipo: TipoDniTrabajador),
            Adj("sin-tipo.pdf", trabajador: Pedro),
            Adj("suelto.pdf"));

        var cut = Pintar();

        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        Texto(cut).Should().Contain("Adjuntos del correo");
        // Con destino y tipo, pero sin fecha de emisión, no está «Listo»: las fechas las escribe una persona.
        Texto(cut).Should().Contain("3 adjuntos: 2 por revisar, 1 sin destino");
        Texto(cut).Should().Contain("Requieren decisión · 3");
        cut.FindAll(".adjb-e").Select(e => e.TextContent.Trim()).Should().BeEquivalentTo("Revisar", "Revisar", "Sin destino");
        Botones(cut).Should().Contain(["Plegar los adjuntos", "Por revisar primero", "Por Trabajador", "Revisar todos los adjuntos"]);
        cut.Find("a[href^='/comunicaciones/adjuntos/']").GetAttribute("target").Should().Be("_blank");
    }

    [Fact]
    public void Con_la_consulta_del_reparto_vacia_no_se_pinta_el_bloque()
    {
        // Solo fija la pantalla: con una lista vacía no inventa nada. Que la consulta salga vacía para el rol Cliente de portal,
        // para un hilo fuera de la Asignación de Cartera o de otro Tenant, y que los comandos lo rechacen, lo prueban
        // RepartoAdjuntosTests (Application) y RepartoAdjuntosBajoRlsTests (integración), no este mediador simulado.
        Preparar(Roles.GestorCae);

        var cut = Pintar();

        cut.FindAll(".adjb").Should().BeEmpty();
    }

    [Fact]
    public void Si_el_reparto_no_se_puede_cargar_se_avisa_en_el_sitio_sin_tumbar_la_pagina()
    {
        var mediador = Preparar(Roles.GestorCae, Adj("a.pdf"));
        mediador.RepartoFalla = true;

        var cut = Pintar();

        cut.WaitForAssertion(() => cut.Find("[role='alert']").TextContent.Should().Contain("No pudimos cargar el reparto de adjuntos"));
        cut.FindAll(".adjb").Should().BeEmpty();

        mediador.RepartoFalla = false;
        cut.Find("[role='alert'] button").Click();

        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        cut.FindAll("[role='alert']").Should().BeEmpty();
    }

    [Fact]
    public void Si_falla_la_recarga_tras_asignar_las_filas_viejas_avisan_y_no_admiten_acciones_hasta_releer()
    {
        var listo = Adj("dni-marta.pdf", trabajador: Marta, tipo: TipoDniTrabajador);
        var otro = Adj("suelto.pdf");
        var mediador = Preparar(Roles.GestorCae, listo, otro);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "dni-marta.pdf", "2026-03-01");
        // Un menú «Más» abierto antes del fallo sigue abierto después: sus acciones también deben esperar.
        cut.Find("button[aria-expanded][aria-label*='suelto.pdf']").Click();
        mediador.Lote = c =>
        {
            // El lote asigna, pero la lectura de después ya no responde.
            var resultado = mediador.Todos(c, EstadoAsignacionAdjunto.Asignado);
            mediador.RepartoFalla = true;
            return resultado;
        };

        cut.Find(".adjb-p .adjb-g button").Click();

        cut.WaitForAssertion(() => cut.Find(".adjb [role='alert']").TextContent.Should().Contain("puede estar desactualizado"));
        // La fila vieja sigue a la vista, pero nada se puede tocar: el adjunto ya asignado aparecería como «Listo».
        cut.FindAll(".adjb-e").Should().NotBeEmpty();
        cut.Find(".adjb-p .adjb-g button").HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".adjb select").Should().NotBeEmpty().And.OnlyContain(s => s.HasAttribute("disabled"));
        cut.FindAll(".adjb input").Should().NotBeEmpty().And.OnlyContain(s => s.HasAttribute("disabled"));
        cut.FindAll(".adjb-p button").Should().OnlyContain(b => b.HasAttribute("disabled"));
        cut.FindAll(".adjb-m button").Should().HaveCount(3).And.OnlyContain(b => b.HasAttribute("disabled"));
        cut.FindAll(".adjb-m button").Single(b => b.TextContent.Trim() == "Ignorar adjunto").Click();
        mediador.De<ResolverAdjuntoCommand>().Should().BeEmpty("ni forzando el clic se actúa sobre filas desfasadas");

        mediador.RepartoFalla = false;
        cut.FindAll(".adjb [role='alert'] button").Single(b => b.TextContent.Trim() == "Reintentar").Click();

        cut.WaitForAssertion(() => cut.FindAll(".adjb [role='alert']").Should().BeEmpty());
        cut.Find("select[aria-label='Destino de suelto.pdf']").HasAttribute("disabled").Should().BeFalse();
        mediador.De<AsignarAdjuntosLoteCommand>().Should().ContainSingle("el bloqueo no reenvía nada");
    }

    public static TheoryData<Type, bool> SelectoresQueFallan => new()
    {
        { typeof(ObtenerTrabajadoresParaSelectorQuery), false },
        { typeof(ObtenerEmpresasParaSelectorQuery), true },
        { typeof(ObtenerTiposDocumentoQuery), false }
    };

    [Theory]
    [MemberData(nameof(SelectoresQueFallan))]
    public void Si_un_selector_no_se_puede_leer_no_se_aprueba_ni_en_lote_ni_uno_a_uno_y_Reintentar_lo_devuelve(Type selector, bool aEmpresa)
    {
        // Con identificadores y fecha, pero sin poder nombrar el destino o el tipo: aprobarlo sería a ciegas.
        var adjunto = aEmpresa
            ? Adj("doc.pdf", empresa: EmpresaPropia, tipo: TipoSeguroEmpresa)
            : Adj("doc.pdf", trabajador: Marta, tipo: TipoDniTrabajador);
        var mediador = Preparar(Roles.GestorCae, adjunto);
        mediador.SelectoresFallan.Add(selector);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "doc.pdf", "2026-03-01");

        cut.Find("[data-selectores='fallo']").TextContent.Should().Contain("No pudimos cargar los nombres");
        cut.Find(".adjb-e").TextContent.Trim().Should().Be("Revisar");
        Texto(cut).Should().Contain("No se puede asignar hasta cargar los nombres del destino y del tipo.").And.NotContain("1 listo");
        cut.Find(".adjb-p .adjb-g button").HasAttribute("disabled").Should().BeTrue("el lote no lleva lo que no se puede nombrar");

        cut.FindAll(".adjb-p button").Single(b => b.TextContent.Contains("Revisar todos")).Click();
        cut.Find(".adjb-uno strong").TextContent.Should().Be("doc.pdf");
        cut.FindAll(".adjb-uno-pie .boton-primario").Single().HasAttribute("disabled").Should().BeTrue("uno a uno tampoco se aprueba");
        mediador.De<AsignarAdjuntosLoteCommand>().Should().BeEmpty();

        mediador.SelectoresFallan.Clear();
        cut.Find("[data-selectores='fallo'] button").Click();

        cut.WaitForAssertion(() => cut.FindAll("[data-selectores]").Should().BeEmpty());
        var asignar = cut.FindAll(".adjb-uno-pie .boton-primario").Single();
        asignar.HasAttribute("disabled").Should().BeFalse("con los nombres a la vista ya se puede aprobar");
        asignar.Click();
        cut.WaitForAssertion(() => mediador.De<AsignarAdjuntosLoteCommand>().Should().ContainSingle());
        mediador.De<AsignarAdjuntosLoteCommand>().Single().Adjuntos.Select(a => a.AdjuntoId).Should().Equal(adjunto.Id);
    }

    [Fact]
    public async Task Una_lectura_de_selectores_que_ya_no_es_la_ultima_no_pisa_los_nombres_aunque_falle_despues()
    {
        var mediador = Preparar(Roles.GestorCae, Adj("doc.pdf", trabajador: Marta, tipo: TipoDniTrabajador));
        var retenida = new TaskCompletionSource<IReadOnlyList<TrabajadorSelectorDto>>();
        mediador.RetenerTrabajadores = retenida;
        var cut = Pintar();
        cut.FindAll(".adjb").Should().BeEmpty("la primera carga sigue esperando al selector de Trabajadores");

        // La página relee el hilo mientras tanto: la segunda carga lee sus propios selectores y pinta la fila.
        cut.Render(p => p.Add(x => x.Recarga, new object()));
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "doc.pdf", "2026-03-01");
        cut.WaitForAssertion(() => cut.Find(".adjb-e").TextContent.Trim().Should().Be("Listo"));

        // La lectura vieja falla al final. Sin la guarda de generación dejaría el aviso y los nombres vacíos sobre filas «Listo».
        await cut.InvokeAsync(() => retenida.SetException(new InvalidOperationException("tarde")));
        // La primera carga siguió tras el fallo: es la que pide las Empresas por segunda vez.
        cut.WaitForAssertion(() => mediador.De<ObtenerEmpresasParaSelectorQuery>().Should().HaveCount(2));
        await cut.InvokeAsync(() => { });

        cut.FindAll("[data-selectores]").Should().BeEmpty();
        Texto(cut).Should().Contain("Marta Ruiz: DNI");
        cut.Find(".adjb-e").TextContent.Trim().Should().Be("Listo");
    }

    [Theory]
    [InlineData(Roles.Consulta, EstadoRepartoAdjunto.Pendiente)]
    [InlineData(Roles.GestorCae, EstadoRepartoAdjunto.Asignado)]
    public void El_aviso_de_selectores_solo_sale_si_hay_algo_que_la_persona_podria_asignar(string rol, EstadoRepartoAdjunto estado)
    {
        var mediador = Preparar(rol, Adj("doc.pdf", trabajador: Marta, tipo: TipoDniTrabajador, estado: estado));
        mediador.SelectoresFallan.Add(typeof(ObtenerTrabajadoresParaSelectorQuery));

        var cut = Pintar();

        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        cut.FindAll("[data-selectores]").Should().BeEmpty();
    }

    [Fact]
    public void Un_destino_que_no_esta_en_el_selector_no_queda_listo_y_pide_elegir_uno_de_la_lista()
    {
        // Los selectores se leen bien, pero el destino propuesto no está en la cartera que ofrecen: tampoco se puede nombrar.
        var nuevo = Guid.NewGuid();
        var mediador = Preparar(Roles.GestorCae, Adj("doc.pdf", trabajador: nuevo, tipo: TipoDniTrabajador));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        // Sin fecha, lo que le falta no es la fecha sino un destino que se pueda nombrar: no cuenta entre los que esperan fecha.
        Texto(cut).Should().NotContain("espera su fecha de emisión");
        EscribirEmision(cut, "doc.pdf", "2026-03-01");

        cut.FindAll("[data-selectores='fallo']").Should().BeEmpty("no falló ningún selector: no se dice que no se pudieran cargar");
        cut.Find("[data-selectores='no-identificados']").TextContent.Should().NotContain("No pudimos cargar los nombres");
        cut.Find(".adjb-e").TextContent.Trim().Should().Be("Revisar");
        Texto(cut).Should().Contain("Destino actual").And.Contain("elige uno de la lista para poder asignarlo");
        cut.Find(".adjb-p .adjb-g button").HasAttribute("disabled").Should().BeTrue();

        // El Trabajador entra en la cartera después de abrir el hilo: la recarga siguiente vuelve a leer los selectores y lo nombra.
        mediador.Trabajadores.Add(new(nuevo, "Lucía Mora", null, null));
        cut.Render(p => p.Add(x => x.Recarga, new object()));

        cut.WaitForAssertion(() => cut.Find(".adjb-e").TextContent.Trim().Should().Be("Listo"));
        Texto(cut).Should().Contain("Lucía Mora");
        cut.Find(".adjb-p .adjb-g button").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void Un_destino_que_no_esta_en_el_selector_ofrece_Reintentar_que_relee_los_selectores()
    {
        // Las lecturas salieron bien, pero el destino propuesto no aparece: la fila avisa y bloquea, y la persona puede releer las listas.
        var nuevo = Guid.NewGuid();
        var mediador = Preparar(Roles.GestorCae, Adj("doc.pdf", trabajador: nuevo, tipo: TipoDniTrabajador));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "doc.pdf", "2026-03-01");
        cut.Find(".adjb-e").TextContent.Trim().Should().Be("Revisar");
        var lecturas = mediador.De<ObtenerTrabajadoresParaSelectorQuery>().Count();

        mediador.Trabajadores.Add(new(nuevo, "Lucía Mora", null, null));
        cut.Find("[data-selectores='no-identificados'] button").Click();

        cut.WaitForAssertion(() => cut.Find(".adjb-e").TextContent.Trim().Should().Be("Listo"));
        mediador.De<ObtenerTrabajadoresParaSelectorQuery>().Count().Should().BeGreaterThan(lecturas, "Reintentar relee los selectores");
        cut.FindAll("[data-selectores]").Should().BeEmpty();
        Texto(cut).Should().Contain("Lucía Mora");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Un_tipo_de_otra_clase_que_el_destino_no_queda_listo_aunque_exista_en_el_catalogo(bool aEmpresa)
    {
        // Un Trabajador con un tipo de Empresa (o al revés): el tipo existe, pero no es de los que se ofrecen para ese destino.
        var adjunto = aEmpresa
            ? Adj("doc.pdf", empresa: EmpresaPropia, tipo: TipoDniTrabajador)
            : Adj("doc.pdf", trabajador: Marta, tipo: TipoSeguroEmpresa);
        var mediador = Preparar(Roles.GestorCae, adjunto);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "doc.pdf", "2026-03-01");

        cut.Find(".adjb-e").TextContent.Trim().Should().Be("Revisar");
        Texto(cut).Should().Contain("no está entre los que puedes elegir: elige uno de la lista").And.NotContain("1 listo");
        cut.Find(".adjb-p .adjb-g button").HasAttribute("disabled").Should().BeTrue("el lote no lleva un tipo que no es de la clase del destino");
        cut.FindAll(".adjb-p button").Single(b => b.TextContent.Contains("Revisar todos")).Click();
        cut.FindAll(".adjb-uno-pie .boton-primario").Single().HasAttribute("disabled").Should().BeTrue("uno a uno tampoco se aprueba");
        mediador.De<AsignarAdjuntosLoteCommand>().Should().BeEmpty();
        cut.Find("[data-selectores='no-identificados'] button").TextContent.Trim().Should().Be("Reintentar");

        // Elegir un tipo de la clase del destino lo deja listo.
        cut.FindAll(".adjb-uno-cab button").Single(b => b.TextContent.Trim() == "Cerrar").Click();
        cut.Find("select[aria-label='Tipo de documento de doc.pdf']").Change((aEmpresa ? TipoSeguroEmpresa : TipoDniTrabajador).ToString());

        cut.WaitForAssertion(() => cut.Find(".adjb-e").TextContent.Trim().Should().Be("Listo"));
        cut.FindAll("[data-selectores]").Should().BeEmpty();
    }

    // ---------- Por rol ----------

    [Fact]
    public void Con_rol_Consulta_se_ve_el_reparto_con_las_acciones_desactivadas_y_la_nota_de_la_maqueta()
    {
        Preparar(Roles.Consulta,
            Adj("a.pdf", trabajador: Marta, tipo: TipoDniTrabajador),
            Adj("b.pdf"),
            Adj("c.pdf", estado: EstadoRepartoAdjunto.Rechazado, motivo: "No es de esta obra"));

        var cut = Pintar();

        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        Texto(cut).Should().Contain("Con el rol Consulta puedes ver el reparto, pero no cambiarlo.");
        cut.FindAll(".adjb select").Should().OnlyContain(s => s.HasAttribute("disabled"));
        cut.FindAll(".adjb input").Should().OnlyContain(s => s.HasAttribute("disabled"));
        foreach (var boton in cut.FindAll(".adjb-a button, .adjb-p button"))
            boton.HasAttribute("disabled").Should().BeTrue($"«{boton.TextContent.Trim()}» no se ofrece a Consulta");
    }

    [Fact]
    public void Con_rol_de_escritura_no_aparece_la_nota_de_solo_consulta_y_las_acciones_estan_activas()
    {
        Preparar(Roles.GestorCae, Adj("b.pdf"));

        var cut = Pintar();

        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        Texto(cut).Should().NotContain("Con el rol Consulta");
        cut.Find(".adjb-a button").HasAttribute("disabled").Should().BeFalse();
        cut.Find("select[aria-label='Destino de b.pdf']").HasAttribute("disabled").Should().BeFalse();
    }

    // ---------- Fijar destino y tipo ----------

    [Fact]
    public void Elegir_un_destino_lo_fija_con_el_comando_de_I4_y_la_fila_pasa_a_revisar()
    {
        var adjunto = Adj("suelto.pdf");
        var mediador = Preparar(Roles.GestorCae, adjunto);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("select[aria-label='Destino de suelto.pdf']").Change($"t:{Marta}");

        cut.WaitForAssertion(() => cut.Find(".adjb-e").TextContent.Trim().Should().Be("Revisar"));
        mediador.De<FijarDestinoAdjuntoCommand>().Should().ContainSingle()
            .Which.Should().Be(new FijarDestinoAdjuntoCommand(adjunto.Id, Marta, null, null));
    }

    [Fact]
    public void El_tipo_solo_ofrece_los_del_ambito_del_destino()
    {
        Preparar(Roles.GestorCae, Adj("de-marta.pdf", trabajador: Marta), Adj("de-empresa.pdf", empresa: EmpresaPropia));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("select[aria-label='Tipo de documento de de-marta.pdf'] option[value='" + TipoDniTrabajador + "']").Should().NotBeNull();
        cut.FindAll("select[aria-label='Tipo de documento de de-marta.pdf'] option[value='" + TipoSeguroEmpresa + "']").Should().BeEmpty();
        cut.Find("select[aria-label='Tipo de documento de de-empresa.pdf'] option[value='" + TipoSeguroEmpresa + "']").Should().NotBeNull();
        cut.FindAll("select[aria-label='Tipo de documento de de-empresa.pdf'] option[value='" + TipoDniTrabajador + "']").Should().BeEmpty();
    }

    [Fact]
    public void Cambiar_a_un_destino_de_otro_ambito_quita_el_tipo_que_ya_no_le_corresponde()
    {
        var adjunto = Adj("a.pdf", trabajador: Marta, tipo: TipoDniTrabajador);
        var mediador = Preparar(Roles.GestorCae, adjunto);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("select[aria-label='Destino de a.pdf']").Change($"e:{EmpresaPropia}");

        cut.WaitForAssertion(() => mediador.De<FijarDestinoAdjuntoCommand>().Should().ContainSingle());
        mediador.De<FijarDestinoAdjuntoCommand>().Single().Should().Be(new FijarDestinoAdjuntoCommand(adjunto.Id, null, EmpresaPropia, null));
    }

    [Fact]
    public void Elegir_el_tipo_conserva_el_destino()
    {
        var adjunto = Adj("a.pdf", trabajador: Marta);
        var mediador = Preparar(Roles.GestorCae, adjunto);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("select[aria-label='Tipo de documento de a.pdf']").Change(TipoDniTrabajador.ToString());

        cut.WaitForAssertion(() => mediador.De<FijarDestinoAdjuntoCommand>().Should().ContainSingle());
        mediador.De<FijarDestinoAdjuntoCommand>().Single().Should().Be(new FijarDestinoAdjuntoCommand(adjunto.Id, Marta, null, TipoDniTrabajador));
    }

    // ---------- Rechazar, descartar, deshacer ----------

    [Fact]
    public void Rechazar_pide_el_motivo_y_lo_envia_con_el_comando()
    {
        var adjunto = Adj("a.pdf", trabajador: Marta, tipo: TipoDniTrabajador);
        var mediador = Preparar(Roles.GestorCae, adjunto);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("button[aria-label='Más acciones de a.pdf']").Click();
        cut.FindAll(".adjb-m button").Single(b => b.TextContent.Trim() == "Rechazar documento").Click();
        cut.FindAll(".adjb-m button").Single(b => b.TextContent.Trim() == "Confirmar").HasAttribute("disabled").Should().BeTrue();

        cut.Find("input[aria-label='Motivo del rechazo de a.pdf']").Input("No es de esta obra");
        cut.FindAll(".adjb-m button").Single(b => b.TextContent.Trim() == "Confirmar").Click();

        cut.WaitForAssertion(() => mediador.De<ResolverAdjuntoCommand>().Should().ContainSingle());
        mediador.De<ResolverAdjuntoCommand>().Single().Should()
            .Be(new ResolverAdjuntoCommand(adjunto.Id, ResolucionAdjunto.Rechazar, "No es de esta obra"));
    }

    [Fact]
    public void Descartar_no_pide_motivo()
    {
        var adjunto = Adj("firma.png");
        var mediador = Preparar(Roles.GestorCae, adjunto);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("button[aria-label='Más acciones de firma.png']").Click();
        cut.FindAll(".adjb-m button").Single(b => b.TextContent.Trim() == "Ignorar adjunto").Click();

        cut.WaitForAssertion(() => mediador.De<ResolverAdjuntoCommand>().Should().ContainSingle());
        mediador.De<ResolverAdjuntoCommand>().Single().Should().Be(new ResolverAdjuntoCommand(adjunto.Id, ResolucionAdjunto.Descartar));
    }

    [Fact]
    public void Lo_rechazado_y_lo_descartado_se_ven_en_resueltos_y_Deshacer_los_reabre()
    {
        var rechazado = Adj("a.pdf", estado: EstadoRepartoAdjunto.Rechazado, motivo: "No es de esta obra");
        var mediador = Preparar(Roles.GestorCae, rechazado, Adj("pendiente.pdf"));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        Texto(cut).Should().Contain("1 rechazado").And.Contain("Ver resueltos (1)");

        cut.FindAll(".adjb-hc button").Single(b => b.TextContent.Contains("Ver resueltos")).Click();
        Texto(cut).Should().Contain("Resueltos · 1").And.Contain("No es de esta obra");
        cut.FindAll(".adjb-a button").Single(b => b.TextContent.Trim() == "Deshacer").Click();

        cut.WaitForAssertion(() => mediador.De<ResolverAdjuntoCommand>().Should().ContainSingle());
        mediador.De<ResolverAdjuntoCommand>().Single().Should().Be(new ResolverAdjuntoCommand(rechazado.Id, ResolucionAdjunto.Reabrir));
    }

    [Fact]
    public void Con_todo_resuelto_el_bloque_resume_y_el_detalle_se_abre_con_Ver_detalle()
    {
        Preparar(Roles.GestorCae,
            Adj("a.pdf", estado: EstadoRepartoAdjunto.Asignado),
            Adj("b.pdf", estado: EstadoRepartoAdjunto.Descartado));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find(".adjb-r").TextContent.Should().Be("1 asignado, 1 ignorado");
        cut.FindAll(".adjb-w").Should().BeEmpty();
        cut.Find(".adjb-c button").Click();

        cut.FindAll(".adjb-w").Should().HaveCount(2);
        cut.Find(".adjb-c button").TextContent.Trim().Should().Be("Ocultar detalle");
    }

    // ---------- Asignar por lote ----------

    [Fact]
    public void Una_fila_con_destino_y_tipo_pero_sin_fecha_de_emision_no_esta_lista_y_se_explica()
    {
        Preparar(Roles.GestorCae, Adj("dni.pdf", trabajador: Marta, tipo: TipoDniTrabajador));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find(".adjb-e").TextContent.Trim().Should().Be("Revisar");
        Texto(cut).Should().Contain("Indica la fecha de emisión para poder asignarlo.");
        Texto(cut).Should().Contain("1 adjunto con destino y tipo espera su fecha de emisión.");
        cut.Find(".adjb-p .adjb-g button").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Con_la_fecha_de_emision_la_fila_esta_lista_y_el_boton_nombra_cuantos_y_a_quien()
    {
        var uno = Adj("dni-marta.pdf", trabajador: Marta, tipo: TipoDniTrabajador);
        var dos = Adj("seguro.pdf", empresa: EmpresaPropia, tipo: TipoSeguroEmpresa);
        Preparar(Roles.GestorCae, uno, dos);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        EscribirEmision(cut, "dni-marta.pdf", "2026-03-01");
        EscribirEmision(cut, "seguro.pdf", "2026-03-02");

        cut.WaitForAssertion(() => cut.FindAll(".adjb-e").Select(e => e.TextContent.Trim()).Should().OnlyContain(t => t == "Listo"));
        var asignar = cut.Find(".adjb-p .adjb-g button");
        asignar.TextContent.Trim().Should().Be("Asignar 2 listos a 1 Trabajador y 1 Empresa");
        asignar.GetAttribute("title").Should().Be("Una sola aprobación. No envía nada al remitente.");
        asignar.HasAttribute("disabled").Should().BeFalse();
        Texto(cut).Should().Contain("Marta Ruiz: DNI").And.Contain("Obras Norte (Empresa): Seguro de responsabilidad civil");
    }

    [Fact]
    public void Asignar_los_listos_envia_el_lote_con_las_fechas_escritas_y_avisa_de_que_no_se_envia_nada()
    {
        var uno = Adj("dni-marta.pdf", trabajador: Marta, tipo: TipoDniTrabajador);
        var dos = Adj("dni-pedro.pdf", trabajador: Pedro, tipo: TipoDniTrabajador);
        var mediador = Preparar(Roles.GestorCae, uno, dos);
        var cambios = 0;
        var cut = Pintar(alCambiar: () => cambios++);
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "dni-marta.pdf", "2026-03-01");
        cut.Find("input[aria-label='Fecha de vencimiento de dni-marta.pdf']").Change("2027-03-01");
        EscribirEmision(cut, "dni-pedro.pdf", "2026-04-01");
        cut.Find("input[aria-label='No caduca: dni-pedro.pdf']").Change(true);

        cut.Find(".adjb-p .adjb-g button").Click();

        cut.WaitForAssertion(() => mediador.De<AsignarAdjuntosLoteCommand>().Should().ContainSingle());
        var lote = mediador.De<AsignarAdjuntosLoteCommand>().Single();
        lote.ConversacionId.Should().Be(Conversacion);
        lote.Adjuntos.Should().BeEquivalentTo(
        [
            // Con el destino y el tipo que se ven en la fila: el servidor los contrasta con la propuesta vigente.
            new AdjuntoAAsignar(uno.Id, TipoDniTrabajador, Marta, null, new DateOnly(2026, 3, 1), new DateOnly(2027, 3, 1), false),
            new AdjuntoAAsignar(dos.Id, TipoDniTrabajador, Pedro, null, new DateOnly(2026, 4, 1), null, true)
        ]);
        cut.WaitForAssertion(() => cambios.Should().Be(1));
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(t => t.Mensaje.Contains("2 documentos asignados") && t.Mensaje.Contains("no se envía nada al remitente"));
    }

    [Fact]
    public void Lo_que_el_servidor_no_asigna_se_queda_en_su_fila_con_su_motivo_y_lo_demas_se_asigna()
    {
        var bueno = Adj("bueno.pdf", trabajador: Marta, tipo: TipoDniTrabajador);
        var malo = Adj("malo.pdf", trabajador: Pedro, tipo: TipoDniTrabajador);
        var mediador = Preparar(Roles.GestorCae, bueno, malo);
        mediador.Lote = c =>
        {
            var i = mediador.Reparto.FindIndex(a => a.Id == bueno.Id);
            mediador.Reparto[i] = mediador.Reparto[i] with { Estado = EstadoRepartoAdjunto.Asignado };
            IReadOnlyList<ResultadoAsignacionAdjunto> r =
            [
                new(bueno.Id, EstadoAsignacionAdjunto.Asignado, Guid.NewGuid(), null),
                new(malo.Id, EstadoAsignacionAdjunto.Conflicto, null, "El Trabajador ya tiene un Documento vigente de este tipo.")
            ];
            return Result.Exito(r);
        };
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "bueno.pdf", "2026-03-01");
        EscribirEmision(cut, "malo.pdf", "2026-03-01");

        cut.Find(".adjb-p .adjb-g button").Click();

        cut.WaitForAssertion(() => cut.Find($".adjb-w[data-adjunto='{malo.Id}'] [role='alert']").TextContent
            .Should().Contain("ya tiene un Documento vigente"));
        cut.FindAll($".adjb-w[data-adjunto='{bueno.Id}']").Should().BeEmpty("lo asignado sale de la lista en curso");
        Texto(cut).Should().Contain("1 asignado");
    }

    [Fact]
    public void Mas_de_veinte_listos_se_asignan_en_tandas_de_veinte()
    {
        var adjuntos = Enumerable.Range(1, 25).Select(i => Adj($"doc-{i}.pdf", trabajador: Marta, tipo: TipoDniTrabajador)).ToArray();
        var mediador = Preparar(Roles.GestorCae, adjuntos);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        foreach (var a in adjuntos) EscribirEmision(cut, a.NombreArchivo, "2026-03-01");

        cut.Find(".adjb-p .adjb-g button").Click();

        cut.WaitForAssertion(() => mediador.De<AsignarAdjuntosLoteCommand>().Should().HaveCount(2));
        mediador.De<AsignarAdjuntosLoteCommand>().Select(c => c.Adjuntos.Count).Should().Equal(20, 5);
    }

    [Fact]
    public void Si_una_tanda_falla_tras_asignar_otras_se_cuenta_lo_asignado_y_la_pagina_se_entera()
    {
        var adjuntos = Enumerable.Range(1, 25).Select(i => Adj($"doc-{i}.pdf", trabajador: Marta, tipo: TipoDniTrabajador)).ToArray();
        var mediador = Preparar(Roles.GestorCae, adjuntos);
        var llamadas = 0;
        mediador.Lote = c => ++llamadas == 1
            ? mediador.Todos(c, EstadoAsignacionAdjunto.Asignado)
            : Result.Fallo<IReadOnlyList<ResultadoAsignacionAdjunto>>(Error.Crear("Lote.Falla", "El lote falló."));
        var cambios = 0;
        var cut = Pintar(() => cambios++);
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        foreach (var a in adjuntos) EscribirEmision(cut, a.NombreArchivo, "2026-03-01");

        cut.Find(".adjb-p .adjb-g button").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<ToastService>().Mensajes
            .Should().Contain(t => t.Mensaje.Contains("20 documentos asignados")));
        cambios.Should().Be(1, "lo asignado por la primera tanda ya existe aunque la segunda falle");
    }

    [Fact]
    public void Si_una_tanda_lanza_tras_asignar_otras_se_cuenta_lo_asignado_una_sola_vez()
    {
        var adjuntos = Enumerable.Range(1, 25).Select(i => Adj($"doc-{i}.pdf", trabajador: Marta, tipo: TipoDniTrabajador)).ToArray();
        var mediador = Preparar(Roles.GestorCae, adjuntos);
        var llamadas = 0;
        mediador.Lote = c => ++llamadas == 1 ? mediador.Todos(c, EstadoAsignacionAdjunto.Asignado) : throw new InvalidOperationException("cae");
        var cambios = 0;
        var cut = Pintar(() => cambios++);
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        foreach (var a in adjuntos) EscribirEmision(cut, a.NombreArchivo, "2026-03-01");

        cut.Find(".adjb-p .adjb-g button").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<ToastService>().Mensajes
            .Should().Contain(t => t.Mensaje.Contains("20 documentos asignados")));
        Services.GetRequiredService<ToastService>().Mensajes.Count(t => t.Mensaje.Contains("20 documentos asignados")).Should().Be(1);
        cambios.Should().Be(1);
    }

    [Fact]
    public void Una_cadena_vacia_en_el_selector_quita_el_destino()
    {
        var adjunto = Adj("a.pdf", trabajador: Marta);
        var mediador = Preparar(Roles.GestorCae, adjunto);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("select[aria-label='Destino de a.pdf']").Change("");

        cut.WaitForAssertion(() => mediador.De<FijarDestinoAdjuntoCommand>().Should().ContainSingle()
            .Which.Should().Be(new FijarDestinoAdjuntoCommand(adjunto.Id, null, null, null)));
    }
    [Fact]
    public void Un_adjunto_indeterminado_no_se_pinta_como_un_rechazo_corriente()
    {
        var adjunto = Adj("dudoso.pdf", trabajador: Marta, tipo: TipoDniTrabajador);
        var mediador = Preparar(Roles.GestorCae, adjunto);
        mediador.Lote = c => mediador.Todos(c, EstadoAsignacionAdjunto.Indeterminado);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "dudoso.pdf", "2026-03-01");

        cut.Find(".adjb-p .adjb-g button").Click();

        cut.WaitForAssertion(() => cut.Find($".adjb-w[data-adjunto='{adjunto.Id}'] [role='alert']").TextContent
            .Should().Contain("No se pudo confirmar"));
    }

    // ---------- Valores del selector de destino ----------

    [Fact]
    public void Las_opciones_de_destino_llevan_el_prefijo_de_su_clase_y_un_valor_ilegible_no_quita_el_destino()
    {
        var adjunto = Adj("a.pdf", trabajador: Marta);
        var mediador = Preparar(Roles.GestorCae, adjunto);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        var select = "select[aria-label='Destino de a.pdf']";

        cut.Find($"{select} option[value='t:{Pedro}']").Should().NotBeNull();
        cut.Find($"{select} option[value='e:{EmpresaPropia}']").Should().NotBeNull();

        cut.Find(select).Change("basura");

        mediador.De<FijarDestinoAdjuntoCommand>().Should().BeEmpty("solo una cadena vacía quita el destino");
    }
    // ---------- Agrupación y uno a uno ----------

    [Fact]
    public void Por_Trabajador_agrupa_los_adjuntos_por_su_destino()
    {
        Preparar(Roles.GestorCae,
            Adj("a.pdf", trabajador: Marta), Adj("b.pdf", trabajador: Marta), Adj("c.pdf", trabajador: Pedro), Adj("d.pdf"));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.FindAll(".adjb-hc button").Single(b => b.TextContent.Trim() == "Por Trabajador").Click();

        var grupos = cut.FindAll("[data-grupo] > span:first-child").Select(g => g.TextContent.Trim()).ToList();
        grupos.Should().Equal("Marta Ruiz · 2", "Pedro Gil · 1", "Sin destino · 1");
    }

    [Fact]
    public void Dos_Trabajadores_homonimos_se_distinguen_en_el_selector_y_no_se_agrupan_juntos()
    {
        var otraMarta = Guid.NewGuid();
        var mediador = Preparar(Roles.GestorCae, Adj("a.pdf", trabajador: Marta), Adj("b.pdf", trabajador: otraMarta));
        mediador.Trabajadores = [new(Marta, "Marta Ruiz", null, "Obras Norte"), new(otraMarta, "Marta Ruiz", null, "Excavaciones Sur")];
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.FindAll("select[aria-label='Destino de a.pdf'] option").Select(o => o.TextContent.Trim())
            .Should().Contain(["Marta Ruiz (Obras Norte)", "Marta Ruiz (Excavaciones Sur)"]);
        cut.FindAll(".adjb-hc button").Single(b => b.TextContent.Trim() == "Por Trabajador").Click();
        cut.FindAll("[data-grupo]").Should().HaveCount(2);
    }

    [Fact]
    public void En_uno_a_uno_una_fila_que_el_servidor_no_asigna_no_avanza_a_la_siguiente()
    {
        var primero = Adj("a.pdf", trabajador: Marta);
        var mediador = Preparar(Roles.GestorCae, primero, Adj("b.pdf"));
        mediador.Lote = c => Result.Exito<IReadOnlyList<ResultadoAsignacionAdjunto>>(
            [new(primero.Id, EstadoAsignacionAdjunto.Conflicto, null, "Ya hay un Documento vigente.")]);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        cut.FindAll(".adjb-p button").Single(b => b.TextContent.Contains("Revisar todos")).Click();
        cut.Find(".adjb-uno strong").TextContent.Should().Be("a.pdf");
        cut.Find(".adjb-uno select[aria-label='Tipo de documento de a.pdf']").Change(TipoDniTrabajador.ToString());
        cut.Find(".adjb-uno input[aria-label='Fecha de emisión de a.pdf']").Change("2026-03-01");

        cut.FindAll(".adjb-uno-pie .boton-primario").Single().Click();

        cut.WaitForAssertion(() => mediador.De<AsignarAdjuntosLoteCommand>().Should().ContainSingle());
        cut.Find(".adjb-uno strong").TextContent.Should().Be("a.pdf");
    }

    [Fact]
    public void Revisar_uno_a_uno_recorre_los_pendientes_de_decision_y_se_cierra()
    {
        Preparar(Roles.GestorCae, Adj("a.pdf", trabajador: Marta), Adj("b.pdf"));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.FindAll(".adjb-p button").Single(b => b.TextContent.Contains("Revisar todos")).Click();

        cut.Find(".adjb-uno strong").TextContent.Should().Be("a.pdf");
        cut.Find(".adjb-uno-titulos .m-d").TextContent.Should().Contain("Adjunto 1 de 2");
        cut.FindAll(".adjb-uno-pie button").Single(b => b.TextContent.Trim() == "Siguiente").Click();
        cut.Find(".adjb-uno strong").TextContent.Should().Be("b.pdf");
        cut.Find(".adjb-uno-cab button").Click();
        cut.FindAll(".adjb-uno").Should().BeEmpty();
        cut.FindAll(".adjb-w").Should().HaveCount(2);
    }

    [Fact]
    public void Con_rol_Consulta_no_se_puede_abrir_el_uno_a_uno()
    {
        Preparar(Roles.Consulta, Adj("a.pdf", trabajador: Marta));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.FindAll(".adjb-p button").Single(b => b.TextContent.Contains("Revisar todos")).HasAttribute("disabled").Should().BeTrue();
        cut.FindAll(".adjb-uno").Should().BeEmpty();
    }

    [Fact]
    public void Los_listos_pasan_a_su_grupo_sin_esconderse_y_se_pueden_plegar_y_mostrar()
    {
        var listo = Adj("listo.pdf", trabajador: Marta, tipo: TipoDniTrabajador);
        Preparar(Roles.GestorCae, listo, Adj("otro.pdf"));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "listo.pdf", "2026-03-01");
        cut.WaitForAssertion(() => cut.FindAll("[data-grupo]").Should().Contain(g => g.TextContent.Contains("Listos · 1")));

        // Al escribir la emisión la fila cambia de grupo pero sigue a la vista: quien corrige el vencimiento no la pierde.
        cut.FindAll(".adjb-w").Should().HaveCount(2);
        cut.FindAll("[data-grupo] button").Single(b => b.TextContent.Trim() == "Plegar los listos").Click();
        cut.FindAll(".adjb-w").Should().HaveCount(1);
        cut.FindAll("[data-grupo] button").Single(b => b.TextContent.Trim() == "Mostrar los listos").Click();

        cut.FindAll(".adjb-w").Should().HaveCount(2);
    }

    // ---------- Lienzo «Pulida: correo con 8 adjuntos» (2026-11-20): pie, agrupación y estados de cada adjunto ----------

    private static string[] TextosDe(AngleSharp.Dom.IElement contenedor, string selector) =>
        contenedor.QuerySelectorAll(selector).Select(e => e.TextContent.Trim()).ToArray();

    [Fact]
    public void El_pie_no_lleva_primario_y_ofrece_asignar_los_listos_y_revisar_todos_los_adjuntos()
    {
        Preparar(Roles.GestorCae, Adj("dni-marta.pdf", trabajador: Marta, tipo: TipoDniTrabajador), Adj("suelto.pdf"));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "dni-marta.pdf", "2026-03-01");
        cut.WaitForAssertion(() => cut.Find(".adjb-p .adjb-g button").TextContent.Should().Contain("1 listo"));

        // Un solo primario por superficie: el del pie es secundario (el primario es «Asignar documento» de la revisión uno a uno).
        cut.FindAll(".adjb-p .boton-primario").Should().BeEmpty();
        var botones = cut.FindAll(".adjb-p .adjb-g button");
        botones.Select(b => b.TextContent.Trim()).Should().Equal("Asignar 1 listo a 1 Trabajador", "Revisar todos los adjuntos");
        botones.Should().OnlyContain(b => b.ClassList.Contains("boton-secundario") && b.ClassList.Contains("boton-pequeno"));
        botones[0].GetAttribute("title").Should().Be("Una sola aprobación. No envía nada al remitente.");
        botones[1].HasAttribute("disabled").Should().BeFalse("queda un adjunto que pide una decisión");
        cut.Find(".adjb-s").TextContent.Should().Be("Marta Ruiz: DNI");
    }

    [Fact]
    public void Sin_adjuntos_que_decidir_Revisar_todos_los_adjuntos_se_desactiva_y_el_pie_desaparece_al_resolverlo_todo()
    {
        Preparar(Roles.GestorCae, Adj("dni-marta.pdf", trabajador: Marta, tipo: TipoDniTrabajador));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "dni-marta.pdf", "2026-03-01");
        cut.WaitForAssertion(() => cut.Find(".adjb-p .adjb-g button").TextContent.Should().Contain("1 listo"));
        cut.FindAll(".adjb-p .adjb-g button")[1].HasAttribute("disabled").Should().BeTrue();

        cut.Find(".adjb-p .adjb-g button").Click();

        cut.WaitForAssertion(() => cut.FindAll(".adjb-p").Should().BeEmpty());
    }

    [Fact]
    public void Cada_estado_de_adjunto_lleva_su_rotulo_y_su_tono_y_solo_Listo_no_es_neutro()
    {
        Preparar(Roles.GestorCae,
            Adj("listo.pdf", trabajador: Marta, tipo: TipoDniTrabajador),
            Adj("revisar.pdf", trabajador: Pedro),
            Adj("sin.pdf"),
            Adj("asignado.pdf", trabajador: Marta, tipo: TipoDniTrabajador, estado: EstadoRepartoAdjunto.Asignado),
            Adj("rechazado.pdf", estado: EstadoRepartoAdjunto.Rechazado, motivo: "No es de esta obra"),
            Adj("ignorado.pdf", estado: EstadoRepartoAdjunto.Descartado));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "listo.pdf", "2026-03-01");
        cut.WaitForAssertion(() => cut.Find(".adjb-r").TextContent.Should().Contain("1 listo"));
        cut.FindAll(".adjb-hc button").Single(b => b.TextContent.Trim().StartsWith("Ver resueltos")).Click();

        string Tono(string archivo) => cut.FindAll(".adjb-w").Single(w => w.TextContent.Contains(archivo))
            .QuerySelector(".adjb-e")!.ClassList.Single(c => c.StartsWith("badge-") && c != "badge-pequeno");
        string Rotulo(string archivo) => cut.FindAll(".adjb-w").Single(w => w.TextContent.Contains(archivo)).QuerySelector(".adjb-e")!.TextContent.Trim();

        new[] { "listo.pdf", "revisar.pdf", "sin.pdf", "asignado.pdf", "rechazado.pdf", "ignorado.pdf" }.Select(Rotulo)
            .Should().Equal("Listo", "Revisar", "Sin destino", "Asignado", "Rechazado", "Ignorado");
        Tono("listo.pdf").Should().Be("badge-exito");
        new[] { "revisar.pdf", "sin.pdf", "asignado.pdf", "rechazado.pdf", "ignorado.pdf" }.Select(Tono).Should().OnlyContain(t => t == "badge-neutro");
    }

    [Fact]
    public void Un_adjunto_asignado_se_ve_resuelto_con_su_destino_y_sin_acciones()
    {
        Preparar(Roles.GestorCae, Adj("a.pdf", trabajador: Marta, tipo: TipoDniTrabajador), Adj("asignado.pdf", trabajador: Pedro, tipo: TipoDniTrabajador, estado: EstadoRepartoAdjunto.Asignado));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        cut.FindAll(".adjb-hc button").Single(b => b.TextContent.Trim() == "Ver resueltos (1)").Click();

        var fila = cut.FindAll(".adjb-w").Single(w => w.TextContent.Contains("asignado.pdf"));
        fila.QuerySelector(".adjb-e")!.TextContent.Trim().Should().Be("Asignado");
        fila.QuerySelector(".adjb-v")!.TextContent.Trim().Should().Be("Pedro Gil");
        fila.QuerySelectorAll("select, input, button").Should().BeEmpty("lo asignado no se edita ni se reabre desde aquí");
        TextosDe(fila, "a.boton").Should().Equal("Ver adjunto");
    }

    [Fact]
    public void Un_vencimiento_que_ya_paso_se_dice_en_la_fila_y_ofrece_rechazar_sin_cambiar_la_situacion()
    {
        var adjunto = Adj("seguro.pdf", empresa: EmpresaPropia, tipo: TipoSeguroEmpresa);
        var mediador = Preparar(Roles.GestorCae, adjunto);
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "seguro.pdf", "2020-01-01");
        cut.FindAll(".adjb-w").Single().QuerySelector("[data-vigencia]").Should().BeNull();

        cut.Find("input[aria-label='Fecha de vencimiento de seguro.pdf']").Change("2020-09-30");

        var vigencia = cut.Find("[data-vigencia='caducado']");
        vigencia.TextContent.Trim().Should().Be("Caducó el 30/09/2020");
        vigencia.ClassList.Should().Contain("adjb-v-mal");
        // Sigue «Listo» y asignable: lo que ya caducó se avisa, no se prohíbe (el reparto no inventa esa regla).
        cut.Find(".adjb-e").TextContent.Trim().Should().Be("Listo");
        cut.Find(".adjb-p .adjb-g button").HasAttribute("disabled").Should().BeFalse();
        var accion = cut.Find(".adjb-a button[aria-label='Rechazar el documento seguro.pdf']");
        accion.TextContent.Trim().Should().Be("Rechazar documento");
        cut.FindAll(".adjb-a button").Select(b => b.TextContent.Trim()).Should().NotContain("Elegir destino");

        accion.Click();

        cut.Find("input[aria-label='Motivo del rechazo de seguro.pdf']").Should().NotBeNull();
        mediador.De<ResolverAdjuntoCommand>().Should().BeEmpty("rechazar pide el motivo antes de enviar nada");
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }

    [Fact]
    public void El_dia_del_vencimiento_aun_no_esta_caducado_y_el_dia_siguiente_si()
    {
        Preparar(Roles.GestorCae,
            Adj("hoy.pdf", trabajador: Marta, tipo: TipoDniTrabajador), Adj("ayer.pdf", trabajador: Pedro, tipo: TipoDniTrabajador));
        Services.AddSingleton<TimeProvider>(new RelojFijo(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("input[aria-label='Fecha de vencimiento de hoy.pdf']").Change("2026-11-20");
        cut.Find("input[aria-label='Fecha de vencimiento de ayer.pdf']").Change("2026-11-19");

        cut.FindAll("[data-vigencia='caducado']").Select(e => e.TextContent.Trim()).Should().Equal("Caducó el 19/11/2026");
    }

    [Fact]
    public void Un_vencimiento_pasado_en_un_adjunto_sin_destino_mantiene_Elegir_destino_como_accion_de_fila()
    {
        Preparar(Roles.GestorCae, Adj("suelto.pdf"));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("input[aria-label='Fecha de vencimiento de suelto.pdf']").Change("2020-01-01");

        var acciones = cut.FindAll(".adjb-a button").Select(b => b.TextContent.Trim()).ToArray();
        acciones.Should().Contain("Elegir destino").And.NotContain("Rechazar documento");
    }

    [Fact]
    public void Un_vencimiento_futuro_o_con_No_caduca_no_se_marca_como_caducado()
    {
        Preparar(Roles.GestorCae,
            Adj("futuro.pdf", trabajador: Marta, tipo: TipoDniTrabajador), Adj("eterno.pdf", trabajador: Pedro, tipo: TipoDniTrabajador));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));

        cut.Find("input[aria-label='Fecha de vencimiento de futuro.pdf']").Change("2099-12-31");
        cut.Find("input[aria-label='Fecha de vencimiento de eterno.pdf']").Change("2020-01-01");
        cut.Find("input[aria-label='No caduca: eterno.pdf']").Change(true);

        cut.FindAll("[data-vigencia]").Should().BeEmpty();
    }

    [Fact]
    public void La_fila_pendiente_lleva_Ver_adjunto_el_menu_de_mas_acciones_y_sus_tres_acciones_con_objeto()
    {
        Preparar(Roles.GestorCae, Adj("suelto.pdf"));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        var fila = cut.Find(".adjb-w");

        var ver = fila.QuerySelector("a[aria-label='Ver adjunto suelto.pdf']")!;
        ver.TextContent.Trim().Should().Be("Ver adjunto");
        ver.GetAttribute("target").Should().Be("_blank");
        ver.GetAttribute("href").Should().StartWith("/comunicaciones/adjuntos/");
        var mas = cut.Find("button[aria-label='Más acciones de suelto.pdf']");
        mas.TextContent.Trim().Should().BeEmpty("es un botón de solo icono");
        mas.QuerySelector("svg").Should().NotBeNull();
        mas.GetAttribute("aria-expanded").Should().Be("false");
        TextosDe(fila, ".adjb-a button").Should().Equal("Elegir destino", "");

        mas.Click();

        cut.Find("button[aria-label='Más acciones de suelto.pdf']").GetAttribute("aria-expanded").Should().Be("true");
        TextosDe(cut.Find(".adjb-m"), "button").Should().Equal("Elegir destino", "Rechazar documento", "Ignorar adjunto");
    }

    [Fact]
    public void Las_cabeceras_de_grupo_y_sus_conmutadores_siguen_el_orden_del_lienzo()
    {
        Preparar(Roles.GestorCae,
            Adj("listo.pdf", trabajador: Marta, tipo: TipoDniTrabajador),
            Adj("suelto.pdf"),
            Adj("hecho.pdf", estado: EstadoRepartoAdjunto.Descartado));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        EscribirEmision(cut, "listo.pdf", "2026-03-01");
        cut.WaitForAssertion(() => cut.FindAll("[data-grupo]").Should().Contain(g => g.TextContent.Contains("Listos · 1")));

        cut.FindAll("[data-grupo] > span:first-child").Select(g => g.TextContent.Trim()).Should().Equal("Requieren decisión · 1", "Listos · 1");
        // Los conmutadores van en la cabecera del primer grupo: agrupar y, solo si hay resueltos, «Ver resueltos (n)».
        TextosDe(cut.FindAll("[data-grupo]")[0], ".adjb-hc button").Should().Equal("Por revisar primero", "Por Trabajador", "Ver resueltos (1)");
        TextosDe(cut.FindAll("[data-grupo]")[1], ".adjb-hc button").Should().Equal("Plegar los listos");
        cut.FindAll(".adjb-hc button").Single(b => b.TextContent.Trim() == "Por revisar primero").GetAttribute("aria-pressed").Should().Be("true");

        cut.FindAll(".adjb-hc button").Single(b => b.TextContent.Trim() == "Ver resueltos (1)").Click();

        cut.FindAll("[data-grupo] > span:first-child").Select(g => g.TextContent.Trim()).Should().Equal("Requieren decisión · 1", "Listos · 1", "Resueltos · 1");
    }

    [Fact]
    public void La_cabecera_pliega_y_muestra_los_adjuntos_con_el_objeto_del_lienzo()
    {
        Preparar(Roles.GestorCae, Adj("suelto.pdf"));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        cut.Find(".adjb-c strong").TextContent.Should().Be("Adjuntos del correo");
        cut.Find(".adjb-r").TextContent.Should().Be("1 adjunto: 1 sin destino");

        cut.Find(".adjb-c button").Click();

        cut.Find(".adjb-c button").TextContent.Trim().Should().Be("Mostrar los adjuntos");
        cut.Find(".adjb-c button").GetAttribute("aria-expanded").Should().Be("false");
        cut.FindAll(".adjb-w").Should().BeEmpty();
        cut.FindAll(".adjb-p").Should().BeEmpty();
    }

    [Fact]
    public void La_revision_uno_a_uno_usa_los_verbos_con_objeto_y_no_el_rechazar_y_reclamar_del_Visor()
    {
        Preparar(Roles.GestorCae, Adj("a.pdf", trabajador: Marta, tipo: TipoDniTrabajador));
        var cut = Pintar();
        cut.WaitForAssertion(() => cut.FindAll(".adjb").Should().HaveCount(1));
        cut.FindAll(".adjb-p button").Single(b => b.TextContent.Contains("Revisar todos")).Click();

        var pie = TextosDe(cut.Find(".adjb-uno-pie"), "button");

        pie.Should().Equal("Anterior", "Siguiente", "Rechazar documento", "Ignorar adjunto", "Asignar documento");
        // Un único primario por superficie: en la revisión uno a uno es «Asignar documento» (el pie del bloque no se ve).
        var primarios = cut.FindAll(".adjb .boton-primario");
        primarios.Should().ContainSingle().Which.TextContent.Trim().Should().Be("Asignar documento");
        // Aquí rechazar no reclama nada al remitente: el «Rechazar y reclamar el documento» es del Visor y es otra acción.
        Texto(cut).Should().NotContain("reclamar");
    }
}
