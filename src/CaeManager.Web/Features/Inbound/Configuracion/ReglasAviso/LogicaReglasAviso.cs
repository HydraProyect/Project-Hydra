namespace CaeManager.Web.Features.Inbound.Configuracion.ReglasAviso;

/// <summary>
/// Los textos y las comprobaciones de la pantalla «Reglas de aviso» (pantalla 18 de la maqueta, CA-19), sin estado: cómo se dice
/// cuándo, a quién y por dónde sale una regla, qué falta para guardarla y cómo se ve el correo de ejemplo.
/// </summary>
public static class LogicaReglasAviso
{
    public static readonly string[] EstadosElegibles = ["Rechazado", "Pendiente de subir", "Incompleto", "Vencido · en tolerancia"];
    public static readonly string[] Dias = ["lunes", "martes", "miércoles", "jueves", "viernes"];
    public static readonly string[] Variables = ["{Empresa contratista}", "{Centro}", "{requerimiento}", "{fecha de vencimiento}"];

    public const string EstadoTolerancia = "Vencido · en tolerancia";

    public static string Nombre(DisparadorAviso d) => d switch
    {
        DisparadorAviso.Vencimiento => "Vencimiento",
        DisparadorAviso.EstadoDelRequerimiento => "Estado del requerimiento",
        DisparadorAviso.DocumentoNuevo => "Documento nuevo",
        _ => "Resumen periódico",
    };

    public static string Ayuda(DisparadorAviso d) => d switch
    {
        DisparadorAviso.Vencimiento => "Avisa cuando se acerca la fecha en que vence un requerimiento Vigente.",
        DisparadorAviso.EstadoDelRequerimiento => "Avisa y repite mientras el requerimiento siga en uno de los estados elegidos.",
        DisparadorAviso.DocumentoNuevo => "Avisa al Gestor CAE de que hay un documento nuevo Pendiente de validar.",
        _ => "Un solo correo con lo que importa de la semana, en vez de un aviso por requerimiento.",
    };

    /// <summary>Asunto y cuerpo de partida de cada disparador (la maqueta los cambia solos mientras no se hayan editado).</summary>
    public static (string Asunto, string Cuerpo) Plantilla(DisparadorAviso d) => d switch
    {
        DisparadorAviso.Vencimiento => ("{requerimiento}: vence el {fecha de vencimiento}",
            "Hola, {Empresa contratista}:\n\nEl requerimiento «{requerimiento}» del Centro {Centro} vence el {fecha de vencimiento}. Sube el documento renovado desde TALVEG para mantener el acceso al Centro.\n\nGracias."),
        DisparadorAviso.EstadoDelRequerimiento => ("{requerimiento} sigue pendiente en {Centro}",
            "Hola, {Empresa contratista}:\n\nEl requerimiento «{requerimiento}» del Centro {Centro} todavía no está al día. Entra en TALVEG y súbelo o corrígelo para no perder el acceso.\n\nGracias."),
        DisparadorAviso.DocumentoNuevo => ("Documento nuevo pendiente de validar: {requerimiento}",
            "{Empresa contratista} ha subido un documento para «{requerimiento}» en el Centro {Centro}. Está Pendiente de validar en la cola de Validación."),
        _ => ("Resumen semanal de {Centro}",
            "Resumen de la semana en {Centro}: requerimientos de {Empresa contratista} y de otras Empresas contratistas que vencen, están Rechazados o esperan validación. Ejemplo: «{requerimiento}», con vencimiento el {fecha de vencimiento}."),
    };

    private static string Cada(int n) => n == 1 ? "cada día" : $"cada {n} días";

    private static string Lista(IReadOnlyList<string> xs) =>
        xs.Count <= 1 ? (xs.Count == 0 ? string.Empty : xs[0]) : string.Join(", ", xs.Take(xs.Count - 1)) + " o " + xs[^1];

    public static string Cuando(ReglaAviso r)
    {
        switch (r.Disparador)
        {
            case DisparadorAviso.Vencimiento:
                var baseTexto = r.DiasUmbral == 0 ? "El día que vence"
                    : r.DiasUmbral == 1 ? "1 día antes del vencimiento" : $"{r.DiasUmbral} días antes del vencimiento";
                return r.RepetirCadaDias > 0 && r.DiasUmbral > 0 ? $"{baseTexto} y {Cada(r.RepetirCadaDias)} hasta que vence" : baseTexto;
            case DisparadorAviso.EstadoDelRequerimiento:
                return $"Se repite {Cada(r.RepetirCadaDias)} mientras esté {Lista(r.Estados)}";
            case DisparadorAviso.DocumentoNuevo:
                return "Al llegar un documento nuevo pendiente de validar";
            default:
                return $"Resumen semanal los {r.DiaResumen}";
        }
    }

    public static string Destino(ReglaAviso r) =>
        r.ParaEmpresa && r.ParaGestor ? "Empresa contratista · Gestor CAE (resumen)" : r.ParaEmpresa ? "Empresa contratista" : "Gestor CAE";

    public static string Canal(ReglaAviso r) =>
        r.PorCorreo && r.AlIniciarSesion ? "Correo y aviso al iniciar sesión" : r.PorCorreo ? "Correo" : "Aviso al iniciar sesión";

    public static string Ambito(ReglaAviso r) =>
        r.Centros.Count == 0 ? "Todos los Centros" : r.Centros.Count == 1 ? r.Centros[0] : $"{r.Centros.Count} Centros";

    /// <summary>Lo que falta para poder guardar (vacío si nada). Cada frase completa la de «Para guardar falta: …».</summary>
    public static IReadOnlyList<string> Faltas(BorradorRegla d)
    {
        var f = new List<string>();
        if (string.IsNullOrWhiteSpace(d.Nombre)) f.Add("el nombre");
        if (d.Disparador == DisparadorAviso.Vencimiento)
        {
            if (d.Dias is not (>= 0 and <= 90)) f.Add("los días de umbral (de 0 a 90)");
            if (d.Repeticion is not (>= 0 and <= 30)) f.Add("la repetición (de 0 a 30)");
        }
        if (d.Disparador == DisparadorAviso.EstadoDelRequerimiento)
        {
            if (d.Estados.Count == 0) f.Add("al menos un estado");
            if (d.Repeticion is not (>= 1 and <= 30)) f.Add("la repetición (de 1 a 30 días)");
        }
        if (!d.ParaEmpresa && !d.ParaGestor) f.Add("algún destinatario");
        if (!d.PorCorreo && !d.AlIniciarSesion) f.Add("algún canal");
        if (d.SoloAlgunosCentros && d.Centros.Count == 0) f.Add("al menos un Centro");
        if (string.IsNullOrWhiteSpace(d.Asunto)) f.Add("el asunto");
        if (string.IsNullOrWhiteSpace(d.Cuerpo)) f.Add("el cuerpo");
        return f;
    }

    /// <summary>Sustituye las variables por los datos ficticios de la vista previa; una variable desconocida se deja tal cual.</summary>
    public static string Sustituir(string texto, string empresa, string centro) =>
        System.Text.RegularExpressions.Regex.Replace(texto, @"\{[^}]+\}", m => m.Value switch
        {
            "{Empresa contratista}" => empresa,
            "{Centro}" => centro,
            "{requerimiento}" => "Formación en trabajos en altura",
            "{fecha de vencimiento}" => "25/11/2026",
            _ => m.Value,
        });
}

/// <summary>La regla que se está editando en el panel: un borrador mutable que solo pasa a <see cref="ReglaAviso"/> al guardar.</summary>
public sealed class BorradorRegla
{
    public Guid Id { get; set; }
    public bool Nueva { get; set; }
    public bool DeSerie { get; set; }
    public bool Activa { get; set; } = true;
    public string Nombre { get; set; } = string.Empty;
    public DisparadorAviso Disparador { get; set; } = DisparadorAviso.Vencimiento;
    public int? Dias { get; set; } = 15;
    public int? Repeticion { get; set; } = 0;
    public List<string> Estados { get; set; } = [];
    public string DiaResumen { get; set; } = "lunes";
    public bool ParaEmpresa { get; set; } = true;
    public bool ParaGestor { get; set; }
    public bool PorCorreo { get; set; } = true;
    public bool AlIniciarSesion { get; set; } = true;
    public bool SoloAlgunosCentros { get; set; }
    public List<string> Centros { get; set; } = [];
    public string Asunto { get; set; } = string.Empty;
    public string Cuerpo { get; set; } = string.Empty;

    public static BorradorRegla Nuevo()
    {
        var (asunto, cuerpo) = LogicaReglasAviso.Plantilla(DisparadorAviso.Vencimiento);
        return new BorradorRegla { Id = CaeManager.Domain.Common.IdentificadorEntidad.Nuevo(), Nueva = true, Asunto = asunto, Cuerpo = cuerpo };
    }

    public static BorradorRegla De(ReglaAviso r) => new()
    {
        Id = r.Id, DeSerie = r.DeSerie, Activa = r.Activa, Nombre = r.Nombre, Disparador = r.Disparador, Dias = r.DiasUmbral,
        Repeticion = r.RepetirCadaDias, Estados = [.. r.Estados], DiaResumen = r.DiaResumen, ParaEmpresa = r.ParaEmpresa,
        ParaGestor = r.ParaGestor, PorCorreo = r.PorCorreo, AlIniciarSesion = r.AlIniciarSesion, SoloAlgunosCentros = r.Centros.Count > 0,
        Centros = [.. r.Centros], Asunto = r.Asunto, Cuerpo = r.Cuerpo,
    };

    /// <summary>La regla que se envía: solo lleva los campos de su disparador (lo que quedó de otro disparador no viaja).</summary>
    public ReglaAviso APublica() => new(
        Id, Nombre.Trim(), DeSerie, Disparador,
        Disparador == DisparadorAviso.Vencimiento ? Dias ?? 0 : 0,
        Disparador is DisparadorAviso.Vencimiento or DisparadorAviso.EstadoDelRequerimiento ? Repeticion ?? 0 : 0,
        Disparador == DisparadorAviso.EstadoDelRequerimiento ? Estados.ToArray() : [],
        Disparador == DisparadorAviso.ResumenPeriodico ? DiaResumen : "lunes", ParaEmpresa, ParaGestor,
        PorCorreo, AlIniciarSesion, SoloAlgunosCentros ? Centros.ToArray() : [], Activa, Asunto, Cuerpo);
}
