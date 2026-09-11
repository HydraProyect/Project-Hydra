using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Commands.ActualizarDeteccionTrabajadoresGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarPerfilDocumentoOficialGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarVerificacionIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.CrearTipoDocumento;
using CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTipoDocumentoPorId;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using TiposDocumentoPagina = CaeManager.Web.Features.TiposDocumento.Pages.TiposDocumento;

namespace CaeManager.Web.Tests;

/// <summary>
/// Tipos de documento contra su mockup Gen 2 («Tipos Documento TALVEG.dc.html»).
/// Los estados vacíos por filtro los sigue probando
/// <see cref="TiposDocumentoVacioPorFiltroTests"/>; esto cubre lo que el
/// rediseño añadió o corrigió.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué consultas y comandos llegan al mediador
/// y con qué parámetros —el doble responde según ellos: la lista, según el
/// texto y el cliente de la consulta; las empresas, según el cliente; el
/// detalle, según el Id—, qué se pinta con lo que vuelve, y qué pasa cuando
/// las respuestas llegan fuera de orden (<see cref="TaskCompletionSource{TResult}"/>).
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el aspecto (bUnit no evalúa CSS: que la casilla
/// parezca un interruptor no se prueba aquí), la autorización de la ruta y de
/// los comandos, ni lo que de verdad se pide en cada centro, que decide
/// <c>ResolucionTipoDocumentoCentro</c> en Application. Que el mensaje de
/// confirmación describa ese efecto es una afirmación sobre esa regla, no una
/// prueba de ella.
/// </para>
/// </summary>
public class TiposDocumentoGen2Tests : BunitContext
{
    /// <summary><see cref="Modal"/> y <see cref="Drawer"/> importan dialogo-foco.js.</summary>
    public TiposDocumentoGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid ClienteA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid ClienteB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaA = Guid.Parse("e1e1e1e1-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaB = Guid.Parse("e2e2e2e2-0000-0000-0000-000000000002");
    private static readonly Guid CentroZaragoza = Guid.Parse("c1c1c1c1-0000-0000-0000-000000000001");
    private static readonly Guid CentroTudela = Guid.Parse("c2c2c2c2-0000-0000-0000-000000000002");

    /// <summary>Un centro que el selector no enseña a quien edita (fuera de su alcance).</summary>
    private static readonly Guid CentroFueraDeAlcance = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");

    // ---------------------------------------------------------------- dobles

    private sealed class MediadorControlado(Func<object, Task<object?>> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return (TResponse)(await responder(request))!;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest
        {
            Enviados.Add(request!);
            return Task.CompletedTask;
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return Task.FromResult<object?>(null);
        }

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>
    /// El «servidor» del test. <see cref="Tipos"/> es lo persistido: un comando
    /// que el test deja pasar lo cambia, y la siguiente consulta lo devuelve.
    /// </summary>
    private sealed class Escenario
    {
        public List<ClienteSelectorDto> Clientes { get; } =
            [new(ClienteA, "Refrielectric S.A."), new(ClienteB, "Grupo Arbeko")];

        public Dictionary<Guid, List<EmpresaSelectorDto>> EmpresasPorCliente { get; } = new()
        {
            [ClienteA] = [new(EmpresaA, "Montajes Ebro S.L.")],
            [ClienteB] = [new(EmpresaB, "Elecnor Instalaciones S.A.U.")]
        };

        /// <summary>Los centros que el selector enseña a quien edita.</summary>
        public List<CentroSelectorDto> Centros { get; } =
        [
            new(CentroZaragoza, "Planta Zaragoza", "Refrielectric S.A.", "Montajes Ebro S.L."),
            new(CentroTudela, "Nave logística Tudela", "Refrielectric S.A.", "Montajes Ebro S.L.")
        ];

        public List<TipoDocumentoListaDto> Tipos { get; } = [];

        /// <summary>Filas TipoDocumentoCentro con Incluido=true, por tipo.</summary>
        public Dictionary<Guid, List<Guid>> CentrosDelTipo { get; } = [];

        /// <summary>Qué tipos devuelve la lista al filtrar por cada cliente.</summary>
        public Dictionary<Guid, HashSet<Guid>> TiposDelCliente { get; } = [];

        /// <summary>Decide el desenlace de cada comando; el éxito se aplica a <see cref="Tipos"/>.</summary>
        public Func<object, Result> Decidir { get; set; } = _ => Result.Exito();

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        public Task<object?> Responder(object peticion) =>
            Interceptar(peticion) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientesParaSelectorQuery => Clientes.ToList(),
                ObtenerEmpresasParaSelectorQuery q => q.ClienteId is { } c && EmpresasPorCliente.TryGetValue(c, out var empresas)
                    ? empresas.ToList()
                    : new List<EmpresaSelectorDto>(),
                ObtenerCentrosParaSelectorQuery => Centros.ToList(),
                ObtenerTiposDocumentoQuery q => Filtrar(q),
                ObtenerTipoDocumentoPorIdQuery q => Detalle(q.Id),
                ActualizarLecturaIaGlobalCommand c => Aplicar(c, c.TipoDocumentoId, t => t with { LecturaIaActiva = c.Activa }),
                ActualizarDeteccionTrabajadoresGlobalCommand c => Aplicar(c, c.TipoDocumentoId, t => t with { DeteccionTrabajadoresActiva = c.Activa }),
                ActualizarVerificacionIaGlobalCommand c => Aplicar(c, c.TipoDocumentoId, t => t with { VerificacionIaActiva = c.Activa }),
                ActualizarPerfilDocumentoOficialGlobalCommand c => Aplicar(c, c.TipoDocumentoId, t => t with { PerfilDocumentoOficial = c.Perfil }),
                EditarTipoDocumentoCommand c => Aplicar(c, c.Id, t => t with { Nombre = c.Nombre, Requerido = c.Requerido }),
                CrearTipoDocumentoCommand c => Decidir(c) is { EsExitoso: true } ? Result.Exito(Guid.NewGuid()) : Result.Fallo<Guid>(Decidir(c).Error),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            });

        public List<TipoDocumentoListaDto> Filtrar(ObtenerTiposDocumentoQuery q)
        {
            IEnumerable<TipoDocumentoListaDto> resultado = Tipos;
            if (!string.IsNullOrWhiteSpace(q.Texto))
                resultado = resultado.Where(t => t.Nombre.Contains(q.Texto, StringComparison.OrdinalIgnoreCase)
                    || t.Aliases.Any(a => a.Contains(q.Texto, StringComparison.OrdinalIgnoreCase)));
            if (q.ClienteId is { } clienteId)
                resultado = resultado.Where(t => TiposDelCliente.TryGetValue(clienteId, out var ids) && ids.Contains(t.Id));
            return resultado.OrderBy(t => t.Orden).ToList();
        }

        private TipoDocumentoDetalleDto? Detalle(Guid id) =>
            Tipos.FirstOrDefault(t => t.Id == id) is { } t
                ? new TipoDocumentoDetalleDto(t.Id, t.Nombre, t.VigenciaMeses, t.AplicaVencimientoAutomatico, t.Orden, t.AmbitoAplicacion,
                    t.Requerido, t.Naturaleza, Notas: null, t.Descripcion, t.CriteriosValidacion, t.SeSolicitaA, t.Observaciones,
                    CentrosDelTipo.GetValueOrDefault(id)?.ToList() ?? [], t.Aliases)
                : null;

        private Result Aplicar(object comando, Guid tipoId, Func<TipoDocumentoListaDto, TipoDocumentoListaDto> cambio)
        {
            var resultado = Decidir(comando);
            if (resultado.EsExitoso)
            {
                var indice = Tipos.FindIndex(t => t.Id == tipoId);
                if (indice >= 0)
                    Tipos[indice] = cambio(Tipos[indice]);
            }

            return resultado;
        }
    }

    private static TipoDocumentoListaDto Tipo(
        string nombre, AmbitoAplicacion ambito = AmbitoAplicacion.Empresa, int orden = 1,
        RequisitoDocumental requerido = RequisitoDocumental.No, NaturalezaJuridica naturaleza = NaturalezaJuridica.RequisitoCliente,
        int? vigencia = 12, PerfilDocumentoOficial oficial = PerfilDocumentoOficial.Ninguno, IReadOnlyList<string>? aliases = null) =>
        new(Guid.NewGuid(), nombre, vigencia, vigencia is not null, orden, ambito, requerido, naturaleza,
            Descripcion: null, CriteriosValidacion: null, SeSolicitaA: null, Observaciones: null,
            LecturaIaActiva: false, DeteccionTrabajadoresActiva: false, VerificacionIaActiva: false, oficial, aliases ?? []);

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<TiposDocumentoPagina> Cut, MediadorControlado Mediador) Renderizar(Escenario escenario, bool integrada = false)
    {
        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();

        var cut = Render<TiposDocumentoPagina>(p => p.Add(x => x.IntegradaEnConfiguracion, integrada));
        return (cut, mediador);
    }

    private ToastMensaje UltimoToast() => Services.GetRequiredService<ToastService>().Mensajes.Last();

    private static IElement Fila(IRenderedComponent<TiposDocumentoPagina> cut, string nombre) =>
        cut.FindAll("table.tabla-tipos tbody tr").Single(f => Texto(f.QuerySelector(".nombre-tipo")!) == nombre);

    private static IElement Interruptor(IRenderedComponent<TiposDocumentoPagina> cut, string campo, string nombre) =>
        cut.Find($"input[role=switch][aria-label='{campo} de {nombre}']");

    /// <summary>Todo el texto de la celda del interruptor —lo mismo que mira el E2E con ToHaveText—.</summary>
    private static string TextoInterruptor(IRenderedComponent<TiposDocumentoPagina> cut, string campo, string nombre) =>
        Texto(Interruptor(cut, campo, nombre).ParentElement!);

    private static Task CambiarInterruptor(IRenderedComponent<TiposDocumentoPagina> cut, string campo, string nombre) =>
        Interruptor(cut, campo, nombre).ChangeAsync(new ChangeEventArgs { Value = true });

    private static Task ElegirCliente(IRenderedComponent<TiposDocumentoPagina> cut, Guid clienteId) =>
        cut.FindAll(".barra-filtros select")[0].ChangeAsync(new ChangeEventArgs { Value = clienteId.ToString() });

    private static Task PulsarEditar(IRenderedComponent<TiposDocumentoPagina> cut, string nombre) =>
        cut.Find($"button[aria-label='Editar {nombre}']").ClickAsync(new MouseEventArgs());

    private static Task PulsarNuevoTipo(IRenderedComponent<TiposDocumentoPagina> cut) =>
        cut.FindAll(".acciones-cabecera button").Single(b => Texto(b) == "+ Nuevo tipo").ClickAsync(new MouseEventArgs());

    private static IElement BotonDelPie(IRenderedComponent<TiposDocumentoPagina> cut, string texto) =>
        cut.FindAll(".drawer-pie button").Single(b => Texto(b) == texto);

    private static IElement BotonDelDialogo(IRenderedComponent<TiposDocumentoPagina> cut, string texto) =>
        cut.FindAll(".modal-pie button").Single(b => Texto(b) == texto);

    /// <summary>El control de formulario asociado a una etiqueta del Drawer (CampoTexto/CampoSelect usan label for=).</summary>
    private static IElement ControlDelDrawer(IRenderedComponent<TiposDocumentoPagina> cut, string etiqueta)
    {
        var label = cut.FindAll(".drawer-panel label[for]").Single(l => Texto(l) == etiqueta);
        return cut.Find($"#{label.GetAttribute("for")}");
    }

    /// <summary>CampoTexto rebota 300 ms: InputAsync espera a que el valor llegue a la página.</summary>
    private static Task Escribir(IRenderedComponent<TiposDocumentoPagina> cut, string etiqueta, string valor) =>
        ControlDelDrawer(cut, etiqueta).InputAsync(new ChangeEventArgs { Value = valor });

    private static Task Elegir(IRenderedComponent<TiposDocumentoPagina> cut, string etiqueta, string valor) =>
        ControlDelDrawer(cut, etiqueta).ChangeAsync(new ChangeEventArgs { Value = valor });

    private static string Texto(IElement elemento) => elemento.TextContent.Trim();

    // ---------------------------------------------------------------- tabla

    [Fact]
    public void La_fila_rotula_ambito_vigencia_y_exigencia_como_se_configuraron()
    {
        var escenario = new Escenario();
        escenario.Tipos.AddRange(
        [
            Tipo("Ficha técnica del vehículo", AmbitoAplicacion.Vehiculo, 1, RequisitoDocumental.Si, NaturalezaJuridica.RequisitoCliente, vigencia: null),
            Tipo("Relación nominal de trabajadores", AmbitoAplicacion.Empresa, 2, RequisitoDocumental.Si, NaturalezaJuridica.PracticaSector, vigencia: 1, aliases: ["RNT", "TC2"]),
            Tipo("Formación en PRL", AmbitoAplicacion.Trabajador, 3, RequisitoDocumental.Condicional, NaturalezaJuridica.ObligacionCondicionada),
            Tipo("Protocolo de acceso", AmbitoAplicacion.Cliente, 4, RequisitoDocumental.No, NaturalezaJuridica.Recomendacion, vigencia: null)
        ]);
        var (cut, _) = Renderizar(escenario);

        string[] Celdas(string nombre) => Fila(cut, nombre).QuerySelectorAll("td").Select(Texto).ToArray();

        Celdas("Ficha técnica del vehículo")[2].Should().Be("Vehículo", "antes salía el nombre del enum, «Vehiculo»");
        Celdas("Ficha técnica del vehículo")[3].Should().Be("—");
        Celdas("Relación nominal de trabajadores")[3].Should().Be("1 mes", "antes decía «1 meses»");
        Celdas("Formación en PRL")[3].Should().Be("12 meses");
        Texto(Fila(cut, "Relación nominal de trabajadores").QuerySelector(".alias-tipo")!).Should().Be("RNT, TC2");

        Celdas("Ficha técnica del vehículo")[5].Should().Be("Requisito de cliente");
        Celdas("Relación nominal de trabajadores")[5].Should().Be("Práctica del sector");
        Celdas("Formación en PRL")[5].Should().Be("Si aplica");
        Celdas("Protocolo de acceso")[5].Should().Be("Opcional");

        Celdas("Relación nominal de trabajadores")[7].Should().NotBe("No aplica", "detectar trabajadores es de los tipos de Empresa");
        Celdas("Formación en PRL")[7].Should().Be("No aplica");
        Celdas("Formación en PRL")[8].Should().NotBe("No aplica", "la verificación IA es de los tipos de Trabajador");
        Celdas("Relación nominal de trabajadores")[8].Should().Be("No aplica");
        Fila(cut, "Protocolo de acceso").QuerySelectorAll("select").Should().ContainSingle("el documento oficial es de Empresa o Cliente");
        Celdas("Formación en PRL")[9].Should().Be("No aplica");
    }

    /// <summary>
    /// TALVEG orienta, no impone: la pantalla no sabe qué exige ninguna norma,
    /// solo lo que alguien configuró. La barrera va delante —el formulario
    /// abierto con su vista previa— para que la ausencia no sea verde vacío.
    /// </summary>
    [Fact]
    public async Task Ningun_texto_afirma_una_obligacion_que_la_pantalla_no_puede_saber()
    {
        var escenario = new Escenario();
        escenario.Tipos.Add(Tipo("Evaluación de riesgos", AmbitoAplicacion.Empresa, 1, RequisitoDocumental.Si, NaturalezaJuridica.ObligacionLegal));
        var (cut, _) = Renderizar(escenario);

        await PulsarEditar(cut, "Evaluación de riesgos");

        cut.Find(".vista-previa-exigencia").TextContent.Should().Contain("Obligación legal", "es el valor que se configuró, dicho como tal");
        // La celda de Exigencia (sexta), no el primer badge de la fila, que es el de Vencimiento automático.
        Fila(cut, "Evaluación de riesgos").QuerySelectorAll("td")[5].QuerySelector(".badge")!.GetAttribute("title")
            .Should().Be("¿Se pide? Sí, siempre · ¿Con qué autoridad? Obligación legal. Es el valor general: un centro puede tener su propia configuración.");
        cut.Markup.Should().NotContainAny(["Obligatorio", "obligatorio", "exige la ley", "Una norma lo exige"]);
    }

    [Fact]
    public void Embebida_en_Configuracion_titula_con_h2_y_enlaza_el_nivel_2_sin_salir_del_hub()
    {
        var (cut, _) = Renderizar(new Escenario(), integrada: true);

        cut.Find(".contenido-panel-configuracion h2.titulo-panel-configuracion").TextContent.Trim().Should().Be("Tipos de documento");
        cut.FindAll("h1").Should().BeEmpty("el hub ya tiene su h1");
        cut.Find(".cabecera-pagina-descripcion a").GetAttribute("href").Should().Be("/configuracion/ia");
    }

    [Fact]
    public void Con_ruta_propia_el_titulo_es_h1_y_el_enlace_va_al_selector_de_lectura_ia()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Find(".contenedor-pagina h1").TextContent.Trim().Should().Be("Tipos de documento");
        cut.Find(".cabecera-pagina-descripcion a").GetAttribute("href").Should().Be("/configuracion/lectura-ia");
    }

    // ---------------------------------------------------------------- cambios en línea

    [Fact]
    public async Task Un_interruptor_cambia_en_el_acto_sin_recargar_la_lista_y_dice_Guardado()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        var (cut, mediador) = Renderizar(escenario);
        var consultasAntes = mediador.Enviados.OfType<ObtenerTiposDocumentoQuery>().Count();

        await CambiarInterruptor(cut, "Lectura IA", "Aptitud médica");

        mediador.Enviados.OfType<ActualizarLecturaIaGlobalCommand>().Should().ContainSingle()
            .Which.Should().Be(new ActualizarLecturaIaGlobalCommand(tipo.Id, true));
        mediador.Enviados.OfType<ObtenerTiposDocumentoQuery>().Count().Should().Be(consultasAntes,
            "antes cada interruptor recargaba la lista entera y la tabla se cambiaba por el esqueleto");
        Interruptor(cut, "Lectura IA", "Aptitud médica").HasAttribute("checked").Should().BeTrue();
        TextoInterruptor(cut, "Lectura IA", "Aptitud médica").Should().Be("Guardado ✓");
    }

    [Fact]
    public async Task Si_el_comando_falla_el_interruptor_vuelve_y_el_aviso_dice_por_que()
    {
        var escenario = new Escenario
        {
            Decidir = c => c is ActualizarVerificacionIaGlobalCommand
                ? Result.Fallo(Error.Crear("LecturaIa.SoloAdministrador", "Solo Administrador puede cambiar este interruptor."))
                : Result.Exito()
        };
        escenario.Tipos.Add(Tipo("Aptitud médica", AmbitoAplicacion.Trabajador));
        var (cut, _) = Renderizar(escenario);

        await CambiarInterruptor(cut, "Verificación IA", "Aptitud médica");

        Interruptor(cut, "Verificación IA", "Aptitud médica").HasAttribute("checked").Should().BeFalse(
            "el cambio no se guardó: la casilla no puede quedarse diciendo lo contrario de lo persistido");
        TextoInterruptor(cut, "Verificación IA", "Aptitud médica").Should().Be("No guardado");
        var toast = UltimoToast();
        toast.Tono.Should().Be(TonoToast.Error);
        toast.Mensaje.Should().Be(
            "No se pudo guardar el cambio en «Aptitud médica». Solo Administrador puede cambiar este interruptor. Se ha restaurado el valor anterior.");
    }

    [Fact]
    public async Task Mientras_el_comando_viaja_dice_Guardando_y_un_segundo_cambio_se_ignora()
    {
        var comando = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p is ActualizarLecturaIaGlobalCommand ? comando.Task : null
        };
        escenario.Tipos.Add(Tipo("Aptitud médica", AmbitoAplicacion.Trabajador));
        var (cut, mediador) = Renderizar(escenario);

        // Sin await: el doble retiene el comando.
        var primero = CambiarInterruptor(cut, "Lectura IA", "Aptitud médica");

        TextoInterruptor(cut, "Lectura IA", "Aptitud médica").Should().Be("Guardando…",
            "«Activa» solo puede aparecer cuando el cambio ya está persistido: el E2E espera exactamente ese texto");
        // Tampoco se espera el segundo antes de comprobar: sin la guarda, su
        // comando lo retendría el mismo doble y el test se colgaría en vez de
        // ponerse rojo (medido: blame lo abortó a los 90 s con un resumen verde).
        var segundo = Interruptor(cut, "Lectura IA", "Aptitud médica").ChangeAsync(new ChangeEventArgs { Value = false });
        mediador.Enviados.OfType<ActualizarLecturaIaGlobalCommand>().Should().ContainSingle(
            "el segundo cambio llegó con el primero en vuelo y habría mandado el valor contrario");

        await cut.InvokeAsync(() => comando.SetResult(Result.Exito()));
        await Task.WhenAll(primero, segundo);

        Interruptor(cut, "Lectura IA", "Aptitud médica").HasAttribute("checked").Should().BeTrue();
        TextoInterruptor(cut, "Lectura IA", "Aptitud médica").Should().Be("Guardado ✓");
    }

    [Fact]
    public async Task Una_lista_que_llega_mientras_el_cambio_viaja_no_lo_deshace()
    {
        var comando = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p is ActualizarLecturaIaGlobalCommand ? comando.Task : null
        };
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.TiposDelCliente[ClienteA] = [tipo.Id];
        var (cut, _) = Renderizar(escenario);

        var cambio = CambiarInterruptor(cut, "Lectura IA", "Aptitud médica");
        // La lista vuelve con lo persistido en ese instante: todavía desactivada.
        await ElegirCliente(cut, ClienteA);

        escenario.Tipos[0] = escenario.Tipos[0] with { LecturaIaActiva = true };
        await cut.InvokeAsync(() => comando.SetResult(Result.Exito()));
        await cambio;

        Interruptor(cut, "Lectura IA", "Aptitud médica").HasAttribute("checked").Should().BeTrue(
            "el comando se confirmó después de pintarse esa lista: lo que se ve tiene que ser lo guardado");
    }

    [Fact]
    public async Task Una_lista_pedida_antes_de_confirmarse_el_cambio_y_que_llega_despues_se_vuelve_a_pedir()
    {
        var comando = new TaskCompletionSource<object?>();
        var listaVieja = new TaskCompletionSource<object?>();
        var consultasDelCliente = 0;
        var escenario = new Escenario();
        escenario.Interceptar = p => p switch
        {
            ActualizarLecturaIaGlobalCommand => comando.Task,
            ObtenerTiposDocumentoQuery q when q.ClienteId == ClienteA && ++consultasDelCliente == 1 => listaVieja.Task,
            _ => null
        };
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.TiposDelCliente[ClienteA] = [tipo.Id];
        var (cut, mediador) = Renderizar(escenario);

        var cambio = CambiarInterruptor(cut, "Lectura IA", "Aptitud médica");
        var filtro = ElegirCliente(cut, ClienteA);

        escenario.Tipos[0] = escenario.Tipos[0] with { LecturaIaActiva = true };
        await cut.InvokeAsync(() => comando.SetResult(Result.Exito()));
        await cambio;
        // La lista se leyó antes de guardarse el cambio y llega ahora, con el valor de antes.
        await cut.InvokeAsync(() => listaVieja.SetResult(new List<TipoDocumentoListaDto> { tipo }));
        await filtro;

        consultasDelCliente.Should().Be(2, "esa lista pudo leerse antes del cambio: se pide otra en vez de pintarla");
        mediador.Enviados.OfType<ObtenerTiposDocumentoQuery>().Last().ClienteId.Should().Be(ClienteA);
        Interruptor(cut, "Lectura IA", "Aptitud médica").HasAttribute("checked").Should().BeTrue();
    }

    [Fact]
    public async Task El_documento_oficial_que_no_se_guarda_vuelve_al_anterior()
    {
        var escenario = new Escenario
        {
            Decidir = c => c is ActualizarPerfilDocumentoOficialGlobalCommand
                ? Result.Fallo(Error.Crear("PerfilDocumentoOficial.SoloAdministrador", "Solo Administrador puede cambiar el perfil oficial."))
                : Result.Exito()
        };
        var tipo = Tipo("Relación nominal de trabajadores", AmbitoAplicacion.Empresa);
        escenario.Tipos.Add(tipo);
        var (cut, mediador) = Renderizar(escenario);

        await Fila(cut, "Relación nominal de trabajadores").QuerySelector("select")!
            .ChangeAsync(new ChangeEventArgs { Value = nameof(PerfilDocumentoOficial.Rnt) });

        mediador.Enviados.OfType<ActualizarPerfilDocumentoOficialGlobalCommand>().Should().ContainSingle()
            .Which.Should().Be(new ActualizarPerfilDocumentoOficialGlobalCommand(tipo.Id, PerfilDocumentoOficial.Rnt));
        Fila(cut, "Relación nominal de trabajadores").QuerySelector("select")!.GetAttribute("value")
            .Should().Be(nameof(PerfilDocumentoOficial.Ninguno));
        Texto(Fila(cut, "Relación nominal de trabajadores").QuerySelector(".celda-oficial .aviso-cambio")!).Should().Be("No guardado");
        UltimoToast().Mensaje.Should().Contain("Solo Administrador puede cambiar el perfil oficial.");
    }

    // ---------------------------------------------------------------- carreras de carga

    [Fact]
    public async Task La_lista_de_un_filtro_anterior_que_llega_tarde_se_descarta()
    {
        var listaA = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        var deA = Tipo("Seguro de responsabilidad civil", orden: 1);
        var deB = Tipo("Plan de seguridad y salud", AmbitoAplicacion.Proyecto, orden: 2);
        escenario.Tipos.AddRange([deA, deB]);
        escenario.TiposDelCliente[ClienteA] = [deA.Id];
        escenario.TiposDelCliente[ClienteB] = [deB.Id];
        escenario.Interceptar = p => p is ObtenerTiposDocumentoQuery { ClienteId: var c } && c == ClienteA ? listaA.Task : null;
        var (cut, _) = Renderizar(escenario);

        var eleccionA = ElegirCliente(cut, ClienteA);
        await ElegirCliente(cut, ClienteB);
        await cut.InvokeAsync(() => listaA.SetResult(new List<TipoDocumentoListaDto> { deA }));
        await eleccionA;

        cut.FindAll("table.tabla-tipos .nombre-tipo").Select(Texto)
            .Should().Equal(["Plan de seguridad y salud"], "el cliente elegido es el B: la lista de A llegó tarde y no es suya");
    }

    [Fact]
    public async Task Las_empresas_de_un_cliente_anterior_que_llegan_tarde_no_rellenan_el_selector()
    {
        var empresasA = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p is ObtenerEmpresasParaSelectorQuery { ClienteId: var c } && c == ClienteA ? empresasA.Task : null
        };
        var (cut, _) = Renderizar(escenario);

        var eleccionA = ElegirCliente(cut, ClienteA);
        await ElegirCliente(cut, ClienteB);
        await cut.InvokeAsync(() => empresasA.SetResult(new List<EmpresaSelectorDto> { new(EmpresaA, "Montajes Ebro S.L.") }));
        await eleccionA;

        cut.FindAll(".barra-filtros select")[1].QuerySelectorAll("option").Select(Texto)
            .Should().Equal("Todas las empresas", "Elecnor Instalaciones S.A.U.");
    }

    [Fact]
    public async Task Al_filtrar_la_tabla_se_queda_marcada_como_ocupada_en_vez_de_volver_el_esqueleto()
    {
        var lista = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        escenario.Tipos.Add(Tipo("Seguro de responsabilidad civil"));
        escenario.Interceptar = p => p is ObtenerTiposDocumentoQuery { ClienteId: not null } ? lista.Task : null;
        var (cut, _) = Renderizar(escenario);

        var eleccion = ElegirCliente(cut, ClienteA);

        cut.FindAll(".esqueleto-lista").Should().BeEmpty("el esqueleto es para cuando no hay nada que enseñar");
        cut.Find(".tabla-tipos-marco").GetAttribute("aria-busy").Should().Be("true");

        await cut.InvokeAsync(() => lista.SetResult(new List<TipoDocumentoListaDto>()));
        await eleccion;
    }

    [Fact]
    public async Task Si_la_carga_falla_reintentar_la_recupera()
    {
        var llamadas = 0;
        var escenario = new Escenario();
        escenario.Tipos.Add(Tipo("Seguro de responsabilidad civil"));
        escenario.Interceptar = p => p is ObtenerTiposDocumentoQuery && ++llamadas == 1
            ? Task.FromException<object?>(new InvalidOperationException("Base de datos caída (simulada)."))
            : null;
        var (cut, _) = Renderizar(escenario);

        cut.Find(".estado-vacio h3").TextContent.Trim().Should().Be("No pudimos cargar los tipos de documento");

        await cut.FindAll(".estado-vacio button").Single(b => Texto(b) == "Reintentar").ClickAsync(new MouseEventArgs());

        llamadas.Should().Be(2);
        cut.FindAll("table.tabla-tipos .nombre-tipo").Select(Texto).Should().Equal("Seguro de responsabilidad civil");
    }

    // ---------------------------------------------------------------- formulario

    [Fact]
    public async Task Quitar_Si_siempre_pide_confirmacion_con_el_efecto_y_no_guarda_hasta_confirmar()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        escenario.Tipos.Add(tipo);
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await Elegir(cut, "¿Se pide?", nameof(RequisitoDocumental.No));
        await BotonDelPie(cut, "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().BeEmpty("todavía no se ha confirmado");
        cut.Find(".modal-cuerpo").TextContent.Should().Contain(
            "«Aptitud médica» dejará de pedirse por defecto: los centros que no tengan su propia configuración para este tipo ya no lo pedirán.");

        await BotonDelDialogo(cut, "Guardar y aplicar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle()
            .Which.Requerido.Should().Be(RequisitoDocumental.No);
        cut.FindAll(".drawer-panel").Should().BeEmpty();
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    [Fact]
    public async Task Crear_con_Si_siempre_y_cancelar_la_confirmacion_no_crea_nada_y_deja_el_formulario()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        await PulsarNuevoTipo(cut);
        await Escribir(cut, "Nombre", "Certificado de formación en altura");
        await Elegir(cut, "¿Se pide?", nameof(RequisitoDocumental.Si));
        await BotonDelPie(cut, "Guardar").ClickAsync(new MouseEventArgs());

        cut.Find(".modal-cuerpo").TextContent.Should().Contain(
            "«Certificado de formación en altura» se pedirá en todos los centros, porque «¿Se pide?» es «Sí, siempre».");

        await BotonDelDialogo(cut, "Cancelar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<CrearTipoDocumentoCommand>().Should().BeEmpty();
        cut.FindAll(".modal-contenido").Should().BeEmpty();
        cut.FindAll(".drawer-panel").Should().ContainSingle("cancelar la confirmación no descarta lo escrito");
    }

    /// <summary>
    /// Lectura → DTO → comando que borra por ausencia: un centro que el
    /// selector no enseña no se pudo desmarcar, así que no puede leerse como
    /// quitado.
    /// </summary>
    [Fact]
    public async Task Guardar_sin_cambiar_lo_que_se_pide_no_pregunta_y_reenvia_los_centros_que_no_se_ven()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.CentrosDelTipo[tipo.Id] = [CentroZaragoza, CentroFueraDeAlcance];
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await BotonDelPie(cut, "Guardar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".modal-contenido").Should().BeEmpty("nada de lo que se pide cambia");
        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle()
            .Which.CentroIds.Should().BeEquivalentTo([CentroZaragoza, CentroFueraDeAlcance]);
    }

    [Fact]
    public async Task Desmarcar_un_centro_pide_confirmacion_y_dice_cuantos()
    {
        var escenario = new Escenario();
        var tipo = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador);
        escenario.Tipos.Add(tipo);
        escenario.CentrosDelTipo[tipo.Id] = [CentroZaragoza, CentroFueraDeAlcance];
        var (cut, mediador) = Renderizar(escenario);

        await PulsarEditar(cut, "Aptitud médica");
        await cut.FindAll(".lista-centros label").Single(l => l.TextContent.Contains("Planta Zaragoza"))
            .QuerySelector("input")!.ChangeAsync(new ChangeEventArgs { Value = false });
        await BotonDelPie(cut, "Guardar").ClickAsync(new MouseEventArgs());

        cut.Find(".modal-cuerpo").TextContent.Should().Contain(
            "Se borra la marca de 1 centro(s) donde se pedía expresamente: pasarán a seguir el valor general, «No se pide».");

        await BotonDelDialogo(cut, "Guardar y aplicar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle()
            .Which.CentroIds.Should().BeEquivalentTo([CentroFueraDeAlcance]);
    }

    [Fact]
    public async Task El_resumen_de_centros_no_promete_que_sin_marcar_se_pide_en_todos()
    {
        var (cut, _) = Renderizar(new Escenario());

        await PulsarNuevoTipo(cut);

        Texto(cut.Find(".resumen-centros")).Should().Be(
            "Sin marcar ninguno, cada centro sigue el valor general («No se pide») y no lo pide por defecto.",
            "el mockup decía «vacío = aplica en todos», que con «No se pide» es falso");

        await Elegir(cut, "¿Se pide?", nameof(RequisitoDocumental.Si));

        Texto(cut.Find(".resumen-centros")).Should().StartWith("Sin marcar ninguno, cada centro sigue el valor general («Sí, siempre») y lo pide");
    }

    [Fact]
    public async Task Un_doble_clic_en_Guardar_envia_un_solo_comando()
    {
        var edicion = new TaskCompletionSource<object?>();
        var escenario = new Escenario
        {
            Interceptar = p => p is EditarTipoDocumentoCommand ? edicion.Task : null
        };
        escenario.Tipos.Add(Tipo("Aptitud médica", AmbitoAplicacion.Trabajador));
        var (cut, mediador) = Renderizar(escenario);
        await PulsarEditar(cut, "Aptitud médica");

        // Ninguno de los dos se espera antes de comprobar: sin la guarda, el
        // segundo comando lo retendría el mismo doble y el test se colgaría.
        var primero = BotonDelPie(cut, "Guardar").ClickAsync(new MouseEventArgs());
        var segundo = BotonDelPie(cut, "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviados.OfType<EditarTipoDocumentoCommand>().Should().ContainSingle();

        await cut.InvokeAsync(() => edicion.SetResult(Result.Exito()));
        await Task.WhenAll(primero, segundo);
    }

    [Fact]
    public async Task Pulsar_Editar_en_dos_filas_seguidas_abre_la_segunda()
    {
        var detalleA = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        var a = Tipo("Aptitud médica", AmbitoAplicacion.Trabajador, orden: 1);
        var b = Tipo("Registro de entrega de EPI", AmbitoAplicacion.Trabajador, orden: 2);
        escenario.Tipos.AddRange([a, b]);
        escenario.Interceptar = p => p is ObtenerTipoDocumentoPorIdQuery q && q.Id == a.Id ? detalleA.Task : null;
        var (cut, _) = Renderizar(escenario);

        var aperturaA = PulsarEditar(cut, "Aptitud médica");
        await PulsarEditar(cut, "Registro de entrega de EPI");
        ControlDelDrawer(cut, "Nombre").GetAttribute("value").Should().Be("Registro de entrega de EPI");

        var detalleDeA = new TipoDocumentoDetalleDto(a.Id, a.Nombre, a.VigenciaMeses, a.AplicaVencimientoAutomatico, a.Orden,
            a.AmbitoAplicacion, a.Requerido, a.Naturaleza, null, null, null, null, null, [], []);
        await cut.InvokeAsync(() => detalleA.SetResult(detalleDeA));
        await aperturaA;

        ControlDelDrawer(cut, "Nombre").GetAttribute("value").Should().Be("Registro de entrega de EPI",
            "se pulsó Editar en la segunda fila: el detalle de la primera llegó tarde y no es el que se está editando");
    }

    [Fact]
    public async Task Crear_con_otro_ambito_no_envia_los_centros_marcados_para_Trabajador()
    {
        var (cut, mediador) = Renderizar(new Escenario());

        await PulsarNuevoTipo(cut);
        await Escribir(cut, "Nombre", "Póliza de responsabilidad civil");
        await cut.FindAll(".lista-centros label").Single(l => l.TextContent.Contains("Planta Zaragoza"))
            .QuerySelector("input")!.ChangeAsync(new ChangeEventArgs { Value = true });
        await Elegir(cut, "Ámbito de aplicación", nameof(AmbitoAplicacion.Empresa));
        await BotonDelPie(cut, "Guardar").ClickAsync(new MouseEventArgs());

        var creado = mediador.Enviados.OfType<CrearTipoDocumentoCommand>().Should().ContainSingle().Subject;
        creado.AmbitoAplicacion.Should().Be(AmbitoAplicacion.Empresa);
        creado.CentroIds.Should().BeEmpty("los centros solo se eligen para el ámbito Trabajador y ya no se ven");
    }

    [Fact]
    public async Task Al_editar_el_ambito_se_ve_y_no_se_puede_cambiar()
    {
        var escenario = new Escenario();
        escenario.Tipos.Add(Tipo("Ficha técnica del vehículo", AmbitoAplicacion.Vehiculo));
        var (cut, _) = Renderizar(escenario);

        await PulsarEditar(cut, "Ficha técnica del vehículo");

        Texto(cut.Find(".ambito-fijo-valor")).Should().Be("Vehículo");
        cut.FindAll(".drawer-panel label[for]").Select(Texto).Should().NotContain("Ámbito de aplicación",
            "EditarTipoDocumentoCommand no lleva ámbito: un selector ahí prometería un cambio que no se guarda");
    }
}
