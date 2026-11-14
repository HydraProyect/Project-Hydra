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
        public List<TrabajadorSelectorDto> Trabajadores { get; set; } = [new(Marta, "Marta Ruiz", null, null), new(Pedro, "Pedro Gil", null, null)];

        public IEnumerable<T> De<T>() => Enviados.OfType<T>();

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
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
        Botones(cut).Should().Contain(["Plegar", "Por revisar primero", "Por Trabajador", "Revisar 3 uno a uno"]);
        cut.Find("a[href^='/comunicaciones/adjuntos/']").GetAttribute("target").Should().Be("_blank");
    }

    [Fact]
    public void Sin_adjuntos_o_sin_permiso_para_leer_el_reparto_no_se_pinta_el_bloque()
    {
        // Rol Cliente de portal o hilo fuera de la cartera: la consulta devuelve una lista vacía y la pantalla no inventa nada.
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
        cut.Find("button.mini").HasAttribute("disabled").Should().BeFalse();
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
        cut.FindAll(".adjb-m button").Single(b => b.TextContent.Trim() == "Rechazar").Click();
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
        cut.FindAll(".adjb-m button").Single(b => b.TextContent.Trim() == "Descartar").Click();

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

        cut.Find(".adjb-r").TextContent.Should().Be("1 asignado, 1 descartado");
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
        cut.Find(".adjb-p .boton-primario").HasAttribute("disabled").Should().BeTrue();
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
        var asignar = cut.Find(".adjb-p .boton-primario");
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

        cut.Find(".adjb-p .boton-primario").Click();

        cut.WaitForAssertion(() => mediador.De<AsignarAdjuntosLoteCommand>().Should().ContainSingle());
        var lote = mediador.De<AsignarAdjuntosLoteCommand>().Single();
        lote.ConversacionId.Should().Be(Conversacion);
        lote.Adjuntos.Should().BeEquivalentTo(
        [
            new AdjuntoAAsignar(uno.Id, new DateOnly(2026, 3, 1), new DateOnly(2027, 3, 1), false),
            new AdjuntoAAsignar(dos.Id, new DateOnly(2026, 4, 1), null, true)
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

        cut.Find(".adjb-p .boton-primario").Click();

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

        cut.Find(".adjb-p .boton-primario").Click();

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

        cut.Find(".adjb-p .boton-primario").Click();

        cut.WaitForAssertion(() => Services.GetRequiredService<ToastService>().Mensajes
            .Should().Contain(t => t.Mensaje.Contains("20 documentos asignados")));
        cambios.Should().Be(1, "lo asignado por la primera tanda ya existe aunque la segunda falle");
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

        cut.Find(".adjb-p .boton-primario").Click();

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
        cut.FindAll(".adjb-p button").Single(b => b.TextContent.Contains("uno a uno")).Click();
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

        cut.FindAll(".adjb-p button").Single(b => b.TextContent.Contains("uno a uno")).Click();

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

        cut.FindAll(".adjb-p button").Single(b => b.TextContent.Contains("uno a uno")).HasAttribute("disabled").Should().BeTrue();
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
        cut.FindAll("[data-grupo] button").Single(b => b.TextContent.Trim() == "Plegar").Click();
        cut.FindAll(".adjb-w").Should().HaveCount(1);
        cut.FindAll("[data-grupo] button").Single(b => b.TextContent.Trim() == "Mostrar").Click();

        cut.FindAll(".adjb-w").Should().HaveCount(2);
    }
}
