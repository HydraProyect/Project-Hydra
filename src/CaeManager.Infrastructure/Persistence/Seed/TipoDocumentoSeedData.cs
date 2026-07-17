namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Catálogo maestro real, tomado de la hoja "Parametros" del cuadro de
/// control en Excel que este sistema reemplaza (ver DATABASE.md). Los Id son
/// fijos y deterministas para que las migraciones sean reproducibles.
/// </summary>
public static class TipoDocumentoSeedData
{
    public static readonly (Guid Id, string Nombre, int? VigenciaMeses, bool AplicaVencimiento, int Orden, string? Notas)[] Datos =
    [
        (new Guid("10000000-0000-0000-0000-000000000001"), "Apto médico laboral", 12, true, 1, "Renovación anual estándar."),
        (new Guid("10000000-0000-0000-0000-000000000002"), "EPIS (firma)", 12, true, 2, "Se firman cada año según nota de origen."),
        (new Guid("10000000-0000-0000-0000-000000000003"), "Reciclaje 4h", 48, true, 3, "Cada 4 años, según Dpto. Formación."),
        (new Guid("10000000-0000-0000-0000-000000000004"), "Formación Art. 19", 36, true, 4, "Recordatorio cada 3 años."),
        (new Guid("10000000-0000-0000-0000-000000000005"), "Formación 60h (base convenio)", null, false, 5, "Formación base, no consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000006"), "Formación 20h", null, false, 6, "Mismo curso de convenio que 60h/6h, no consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000007"), "Formación 6h", null, false, 7, "Mismo curso de convenio que 60h/20h, no consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000008"), "Información Art. 18", null, false, 8, "No consta periodicidad de renovación."),
        (new Guid("10000000-0000-0000-0000-000000000009"), "Carretillas elevadoras", null, false, 9, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000A"), "PEMP (plataformas elevadoras)", null, false, 10, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000B"), "LOTO (4h)", null, false, 11, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000C"), "Seguridad alimentaria", null, false, 12, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000D"), "Primeros auxilios", null, false, 13, "Se recomienda revisar cada 2 años; sin dato oficial de origen."),
        (new Guid("10000000-0000-0000-0000-00000000000E"), "Espacios confinados", null, false, 14, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000F"), "Trabajos en altura (8h)", null, false, 15, "Configurable si el convenio interno define vigencia."),
    ];

    /// <summary>Proyección plana usada por HasData (necesita anonymous/objeto con las propiedades de la entidad).</summary>
    public static IEnumerable<object> ComoFilasParaMigracion() =>
        Datos.Select(d => new
        {
            Id = d.Id,
            Nombre = d.Nombre,
            VigenciaMeses = d.VigenciaMeses,
            AplicaVencimientoAutomatico = d.AplicaVencimiento,
            Orden = d.Orden,
            Notas = d.Notas
        });
}
