using System.Globalization;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Inbound.Validacion.Pages;

public partial class ValidacionDocumento
{
    private enum ModoVeredicto
    {
        Ninguno,
        Rechazando,
        Incompleto
    }

    [Parameter] public Guid Id { get; set; }

    [Inject] private IServicioValidacionInbound Servicio { get; set; } = default!;
    [Inject] private ColaEnCursoValidacion ColaEnCurso { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;
    [Inject] private ToastService Toast { get; set; } = default!;

    private static readonly string[] MotivosRechazo =
        ["Documento ilegible", "Documento equivocado", "Datos erróneos", "Fuera de vigencia", "Firma o sello ausente", "Formato no admitido", "Otro"];

    private static readonly string[] Faltas =
        ["Falta una página", "Falta la firma o el sello", "Falta un anexo", "Falta la otra cara", "Otro"];

    private static readonly (string Clave, string Texto)[] FiltrosEntidad =
        [("", "Todos"), (nameof(EntidadValidable.Empresa), "Empresa"), (nameof(EntidadValidable.Trabajador), "Trabajadores"), (nameof(EntidadValidable.Vehiculo), "Vehículos")];

    private readonly HashSet<Guid> _resueltos = [];
    private readonly HashSet<Guid> _destinos = [];

    private DocumentoAValidar? _doc;
    private ColaValidacion? _cola;
    private IReadOnlyList<Guid> _ids = [];
    private Guid _cargadoId;
    private bool _cargando = true;
    private bool _error;
    private bool _terminado;
    private bool _resuelto;
    private bool _enviando;

    private DateOnly? _emision;
    private DateOnly? _vencimiento;
    private bool _emisionNoValida;
    private bool _vencimientoNoValido;

    private ModoVeredicto _modo;
    private string _motivo = MotivosRechazo[0];
    private string _falta = Faltas[0];
    private string _explicacion = string.Empty;

    private bool _confirmandoValidar;
    private bool _ayudaAtajos;
    private bool _pasarAlSiguiente = true;
    private VeredictoRegistrado? _veredicto;
    private Guid _veredictoDocumentoId;
    private string _textoVeredicto = string.Empty;

    private int _pagina;
    private VersionAnterior? _anterior;
    private bool _historialAbierto;
    private bool _criterioExpandido;
    private bool _selectorAbierto;
    private string _busquedaPendientes = string.Empty;
    private string _filtroEntidad = string.Empty;

    private int _indice => Math.Max(0, _ids.ToList().IndexOf(Id));

    private Guid? AnteriorId => Vecino(-1);

    private Guid? SiguienteId => Vecino(1);

    private int HechosHoy => (_cola?.HechosHoy ?? 0) + _resueltos.Count;

    private int _generacion;

    // Sin documento cargado, con la cola terminada o con un veredicto en vuelo no se actúa: los atajos de teclado
    // siguen vivos aunque los botones se oculten, así que la guarda vive aquí y no en el marcado.
    private bool PuedeActuar => _doc is not null && !_resuelto && !_cargando && !_terminado && !_error && !_enviando;

    // D-12: quien subió la versión no la valida, pero sí puede rechazarla. El servidor lo vuelve a decidir
    // (RegistrarEvidenciaInboundCommand); esta guarda solo evita ofrecer lo que rechazaría.
    private bool PuedeValidar => PuedeActuar && _doc is { Propio: false };

    private IReadOnlyList<PaginaDocumento> PaginasVisibles => _anterior?.Paginas ?? _doc?.Paginas ?? [];

    private ResultadoLectura Lectura => _doc is null
        ? ResultadoLectura.Correcto
        : LogicaValidacionInbound.Peor(_doc.DatosAVerificar.Select(d => d.Resultado).Concat(_doc.Chequeos.Select(c => c.Resultado)));

    private IReadOnlyList<(string Etiqueta, string Nota, ResultadoLectura Resultado)> Problemas =>
        _doc is null
            ? []
            : [.. _doc.DatosAVerificar.Where(d => d.Resultado != ResultadoLectura.Correcto).Select(d => (d.Etiqueta, d.Nota ?? LogicaValidacionInbound.TextoIa(d.Resultado), d.Resultado)),
               .. _doc.Chequeos.Where(c => c.Resultado != ResultadoLectura.Correcto).Select(c => (c.Etiqueta, c.Detalle ?? LogicaValidacionInbound.TextoIa(c.Resultado), c.Resultado))];

    private string TituloConfirmacion => Lectura == ResultadoLectura.ConError
        ? "Se han detectado errores en el documento"
        : "Hay datos sin detectar en el documento";

    private int Recuento(ResultadoLectura resultado) =>
        _doc is null ? 0 : _doc.DatosAVerificar.Count(d => d.Resultado == resultado) + _doc.Chequeos.Count(c => c.Resultado == resultado);

    private bool FechaMala => TextoFechaMala is not null;

    private string? TextoFechaMala
    {
        get
        {
            if (_emisionNoValida || _vencimientoNoValido) return "Escribe la fecha como dd/mm/aaaa.";
            if (_emision is { } emision && emision > DiaDeNegocio.Hoy()) return "La fecha de emisión no puede ser futura.";
            if (_emision is { } e && _vencimiento is { } v && v < e) return "El vencimiento no puede ser anterior a la emisión.";
            return null;
        }
    }

    private string ClaseAyuda => _doc is { Propio: true } ? "" : Lectura switch
    {
        ResultadoLectura.Correcto => "ok",
        ResultadoLectura.ConError => "error",
        _ => "aviso"
    };

    private string TextoAyuda
    {
        get
        {
            if (_modo == ModoVeredicto.Rechazando) return "Revisa el motivo y pulsa Rechazar otra vez (R) para confirmar.";
            if (_modo == ModoVeredicto.Incompleto) return "Elige qué falta y pulsa Marcar incompleto otra vez (I) para confirmar.";
            if (_doc is { Propio: true }) return TextosValidacion["SubidoPorTi"];
            if (FechaMala) return "Corrige la fecha para poder validar.";
            return Lectura switch
            {
                ResultadoLectura.Correcto => "Todo correcto: pulsa Espacio para validar.",
                ResultadoLectura.ConError => "Hay datos con error: no se puede validar sin confirmarlo. Recházalo o márcalo como incompleto.",
                _ => "Hay datos sin detectar: revísalos antes de validar."
            };
        }
    }

    private string? SiguienteTexto
    {
        get
        {
            if (SiguienteId is not { } siguiente) return null;
            var resumen = _cola?.Documentos.FirstOrDefault(d => d.Id == siguiente);
            return resumen is null ? "siguiente documento" : $"{resumen.Titular} · {resumen.Requerimiento}";
        }
    }

    private IReadOnlyList<DocumentoPorValidar> Pendientes =>
        _cola is null
            ? []
            : [.. LogicaValidacionInbound.Ordenar(_cola.Documentos.Where(d => !_resueltos.Contains(d.Id)))
                .Where(d => _filtroEntidad == "" || d.Entidad.ToString() == _filtroEntidad)
                .Where(d => string.IsNullOrWhiteSpace(_busquedaPendientes)
                    || LogicaValidacionInbound.Contiene($"{d.Requerimiento} {d.Titular} {d.Identificador} {d.EmpresaContratista}", _busquedaPendientes.Trim()))];

    protected override async Task OnParametersSetAsync()
    {
        if (_cargadoId == Id && _doc is not null) return;
        _cargadoId = Id;
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        // Cada carga tiene su número: si llega otra (J, J seguidos) mientras esta espera, la vieja no pisa a la nueva.
        var generacion = ++_generacion;
        _cargando = true;
        _error = false;
        _terminado = false;
        _resuelto = false;
        _doc = null;
        ReiniciarFormulario();
        try
        {
            var colaRespuesta = await Servicio.ObtenerColaAsync();
            if (generacion != _generacion) return;
            _cola = colaRespuesta.Valor;
            var ordenados = _cola is null ? [] : LogicaValidacionInbound.Ordenar(_cola.Documentos).Select(d => d.Id).ToList();
            var enCurso = ColaEnCurso.Ids;
            _ids = [.. (enCurso.Contains(Id) ? enCurso : ordenados).Where(x => x == Id || !_resueltos.Contains(x))];

            var respuesta = await Servicio.ObtenerDocumentoAsync(Id);
            if (generacion != _generacion) return;
            _doc = respuesta.Valor;
            if (_doc is not null)
            {
                _emision = _doc.Emision;
                _vencimiento = _doc.Vencimiento;
                _motivo = Contiene(MotivosRechazo, _doc.MotivoRechazoSugerido) ?? MotivosRechazo[0];
                _falta = Contiene(Faltas, _doc.FaltaSugerida) ?? Faltas[0];
            }
        }
        catch (Exception)
        {
            if (generacion == _generacion) _error = true;
        }
        finally
        {
            if (generacion == _generacion) _cargando = false;
        }
    }

    private static string? Contiene(string[] opciones, string valor) => opciones.FirstOrDefault(o => o == valor);

    private void ReiniciarFormulario()
    {
        _modo = ModoVeredicto.Ninguno;
        _explicacion = string.Empty;
        _destinos.Clear();
        _pagina = 0;
        _anterior = null;
        _confirmandoValidar = false;
        _emisionNoValida = false;
        _vencimientoNoValido = false;
        _selectorAbierto = false;
    }

    private Guid? Vecino(int paso)
    {
        var lista = _ids.Where(x => x == Id || !_resueltos.Contains(x)).ToList();
        var posicion = lista.IndexOf(Id);
        if (posicion < 0) return null;
        var destino = posicion + paso;
        return destino >= 0 && destino < lista.Count ? lista[destino] : null;
    }

    private void IrAnterior()
    {
        // Con un veredicto en vuelo no se navega: el resultado volvería a un documento que ya no se ve.
        if (!_enviando && AnteriorId is { } id) Navegacion.NavigateTo($"/inbound/validacion/{id}");
    }

    private void IrSiguiente()
    {
        if (!_enviando && SiguienteId is { } id) Navegacion.NavigateTo($"/inbound/validacion/{id}");
    }

    private void AbrirPendiente(Guid id)
    {
        _selectorAbierto = false;
        if (!_enviando) Navegacion.NavigateTo($"/inbound/validacion/{id}");
    }

    private void CambiarEmision(DateOnly? emision)
    {
        _emision = emision;
        // Con vigencia fija el vencimiento se calcula; sin ella, lo escribe el Gestor CAE.
        if (_doc?.VigenciaMeses is { } meses)
            _vencimiento = emision is { } e ? LogicaValidacionInbound.SumarMeses(e, meses) : null;
    }

    private void AlternarDestino(Guid id, bool marcado)
    {
        if (marcado) _destinos.Add(id);
        else _destinos.Remove(id);
    }

    private void VerAnterior(VersionAnterior version)
    {
        _anterior = version;
        _pagina = 0;
    }

    private void VolverAlActual()
    {
        _anterior = null;
        _pagina = 0;
    }

    private static string EstiloCaja(CajaDato caja) =>
        string.Create(CultureInfo.InvariantCulture, $"left:{caja.X * 100:0.##}%;top:{caja.Y * 100:0.##}%;width:{caja.Ancho * 100:0.##}%;height:{caja.Alto * 100:0.##}%");

    private static string TextoVeredicto(VeredictoRegistrado veredicto) => veredicto.Veredicto switch
    {
        VeredictoValidacion.Validado => "Validado",
        VeredictoValidacion.Rechazado => "Rechazado, vuelve a la Empresa contratista",
        _ => "Marcado como incompleto, vuelve a la Empresa contratista"
    };

    // Validar: con errores o datos sin detectar pide confirmarlo; con todo correcto, valida directamente.
    private async Task ValidarPulsado()
    {
        if (!PuedeValidar || FechaMala || _enviando) return;
        if (Lectura != ResultadoLectura.Correcto)
        {
            _confirmandoValidar = true;
            return;
        }

        await EnviarValidarAsync(confirmado: false);
    }

    private Task ConfirmarValidarAsync() => PuedeValidar && !FechaMala ? EnviarValidarAsync(confirmado: true) : Task.CompletedTask;

    private async Task EnviarValidarAsync(bool confirmado)
    {
        if (_doc is null || _enviando) return;
        await Registrar(
            () => Servicio.ValidarAsync(new SolicitudValidar(_doc.Id, _emision, _vencimiento, confirmado, [.. _destinos])),
            confirmado ? "Validado tras confirmarlo" : null);
        _confirmandoValidar = false;
    }

    // Rechazar y Marcar incompleto piden doble pulsación: la primera abre el formulario; la segunda confirma.
    private async Task RechazarPulsado()
    {
        if (!PuedeActuar || _enviando) return;
        if (_modo != ModoVeredicto.Rechazando)
        {
            _modo = ModoVeredicto.Rechazando;
            _explicacion = _doc?.ExplicacionSugerida ?? string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(_explicacion))
        {
            Toast.Mostrar("Explica qué tiene que corregir la Empresa contratista.");
            return;
        }

        await Registrar(() => Servicio.RechazarAsync(new SolicitudRechazar(_doc!.Id, _motivo, _explicacion.Trim())), null);
    }

    private async Task IncompletoPulsado()
    {
        if (!PuedeActuar || _enviando) return;
        if (_modo != ModoVeredicto.Incompleto)
        {
            _modo = ModoVeredicto.Incompleto;
            _explicacion = _doc?.ExplicacionSugerida ?? string.Empty;
            return;
        }

        if (string.IsNullOrWhiteSpace(_explicacion))
        {
            Toast.Mostrar("Explica qué tiene que completar la Empresa contratista.");
            return;
        }

        await Registrar(() => Servicio.MarcarIncompletoAsync(new SolicitudMarcarIncompleto(_doc!.Id, _falta, _explicacion.Trim())), null);
    }

    private void CancelarModo()
    {
        _modo = ModoVeredicto.Ninguno;
        _explicacion = string.Empty;
    }

    private async Task Registrar(Func<Task<RespuestaValidacion<VeredictoRegistrado>>> enviar, string? textoEspecial)
    {
        if (_enviando) return;
        _enviando = true;
        // El documento del veredicto se fija antes del await: si el Gestor CAE navega mientras la petición vuela, el veredicto
        // es de este documento y no del que tenga delante cuando vuelva.
        var documentoId = Id;
        try
        {
            var respuesta = await enviar();
            if (!respuesta.Disponible)
            {
                Toast.Mostrar("El veredicto no se puede registrar todavía: la validación no está disponible.");
                return;
            }

            if (respuesta.Valor is not { } registrado)
            {
                Toast.Mostrar(respuesta.Mensaje ?? "No se pudo registrar el veredicto.");
                return;
            }

            var texto = textoEspecial ?? TextoVeredicto(registrado);
            if (registrado.Deshacible)
            {
                _veredicto = registrado;
                _textoVeredicto = texto;
                _veredictoDocumentoId = documentoId;
            }
            else
            {
                // Veredicto firme, sin plazo de Deshacer: se dice y basta.
                Toast.Mostrar(texto);
            }
            _resueltos.Add(documentoId);
            _modo = ModoVeredicto.Ninguno;

            // Si ya está en otro documento, el aviso de Deshacer queda con el veredicto y no se toca el estado del que ve.
            if (documentoId != Id) return;

            var siguiente = SiguienteId;
            if (_pasarAlSiguiente)
            {
                if (siguiente is { } id)
                    Navegacion.NavigateTo($"/inbound/validacion/{id}");
                else
                    _terminado = true;
            }
            else
            {
                _resuelto = true;
            }
        }
        finally
        {
            _enviando = false;
        }
    }

    private async Task DeshacerAsync()
    {
        if (_veredicto is not { } veredicto) return;
        // El documento del veredicto se fija antes del await: si entre medias se registra otro, no se toca el equivocado.
        var documentoDelVeredicto = _veredictoDocumentoId;
        var respuesta = await Servicio.DeshacerAsync(veredicto.VeredictoId);
        if (!respuesta.Aceptada)
        {
            Toast.Mostrar(respuesta.Mensaje ?? "No se pudo deshacer: ya ha pasado el plazo.");
            if (ReferenceEquals(_veredicto, veredicto)) _veredicto = null;
            return;
        }

        if (ReferenceEquals(_veredicto, veredicto)) _veredicto = null;
        _resueltos.Remove(documentoDelVeredicto);
        if (documentoDelVeredicto == Id)
        {
            _terminado = false;
            _resuelto = false;
            await CargarAsync();
        }
        else
        {
            Navegacion.NavigateTo($"/inbound/validacion/{documentoDelVeredicto}");
        }
    }

    private void CerrarAviso() => _veredicto = null;

    private async Task ManejarAtajoAsync(string tecla)
    {
        if (_ayudaAtajos)
        {
            if (tecla is "Escape" or "?") _ayudaAtajos = false;
            return;
        }

        // Con la confirmación de «Validar con errores» abierta, el teclado solo la cierra: confirmar exige pulsar el botón,
        // para que quien tiene la tecla apretada (o la pulsa dos veces) no la salte sin haberla leído.
        if (_confirmandoValidar)
        {
            if (tecla == "Escape") _confirmandoValidar = false;
            return;
        }

        switch (tecla)
        {
            case "Espacio" or "v":
                await ValidarPulsado();
                break;
            case "r":
                await RechazarPulsado();
                break;
            case "i":
                await IncompletoPulsado();
                break;
            case "j":
                IrSiguiente();
                break;
            case "k":
                IrAnterior();
                break;
            case "?":
                _ayudaAtajos = true;
                break;
            case "Escape":
                CancelarModo();
                _selectorAbierto = false;
                break;
        }
    }
}
