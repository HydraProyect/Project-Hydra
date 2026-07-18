using CaeManager.Domain.Documentos;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Catálogo maestro real, tomado de la hoja "Parametros" del cuadro de
/// control en Excel que este sistema reemplaza (ver DATABASE.md), más los
/// documentos base de Empresa y de Vehículo indicados directamente por el
/// usuario (2026-07). Los Id son fijos y deterministas para que las
/// migraciones sean reproducibles.
/// </summary>
public static class TipoDocumentoSeedData
{
    public static readonly (Guid Id, string Nombre, int? VigenciaMeses, bool AplicaVencimiento, int Orden, AmbitoAplicacion Ambito, bool EsObligatorio, string? Notas)[] Datos =
    [
        // --- Catálogo original de Trabajador (Fase 0, hoja "Parametros" del Excel) ---
        (new Guid("10000000-0000-0000-0000-000000000001"), "Apto médico laboral", 12, true, 1, AmbitoAplicacion.Trabajador, false, "Renovación anual estándar."),
        (new Guid("10000000-0000-0000-0000-000000000002"), "EPIS (firma)", 12, true, 2, AmbitoAplicacion.Trabajador, false, "Se firman cada año según nota de origen."),
        (new Guid("10000000-0000-0000-0000-000000000003"), "Reciclaje 4h", 48, true, 3, AmbitoAplicacion.Trabajador, false, "Cada 4 años, según Dpto. Formación."),
        (new Guid("10000000-0000-0000-0000-000000000004"), "Formación Art. 19", 36, true, 4, AmbitoAplicacion.Trabajador, false, "Recordatorio cada 3 años."),
        (new Guid("10000000-0000-0000-0000-000000000005"), "Formación 60h (base convenio)", null, false, 5, AmbitoAplicacion.Trabajador, false, "Formación base, no consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000006"), "Formación 20h", null, false, 6, AmbitoAplicacion.Trabajador, false, "Mismo curso de convenio que 60h/6h, no consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000007"), "Formación 6h", null, false, 7, AmbitoAplicacion.Trabajador, false, "Mismo curso de convenio que 60h/20h, no consta caducidad."),
        (new Guid("10000000-0000-0000-0000-000000000008"), "Información Art. 18", null, false, 8, AmbitoAplicacion.Trabajador, false, "No consta periodicidad de renovación."),
        (new Guid("10000000-0000-0000-0000-000000000009"), "Carretillas elevadoras", null, false, 9, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000A"), "PEMP (plataformas elevadoras)", null, false, 10, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000B"), "LOTO (4h)", null, false, 11, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000C"), "Seguridad alimentaria", null, false, 12, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000D"), "Primeros auxilios", null, false, 13, AmbitoAplicacion.Trabajador, false, "Se recomienda revisar cada 2 años; sin dato oficial de origen."),
        (new Guid("10000000-0000-0000-0000-00000000000E"), "Espacios confinados", null, false, 14, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),
        (new Guid("10000000-0000-0000-0000-00000000000F"), "Trabajos en altura (8h)", null, false, 15, AmbitoAplicacion.Trabajador, false, "Configurable si el convenio interno define vigencia."),

        // --- Documentos base de Empresa, obligatorios para todos los clientes (2026-07, indicados directamente por el usuario) ---
        (new Guid("20000000-0000-0000-0000-000000000001"), "Certificado de estar al corriente con la Seguridad Social", 1, true, 16, AmbitoAplicacion.Empresa, true, "Mensual."),
        (new Guid("20000000-0000-0000-0000-000000000002"), "Certificado de estar al corriente con Hacienda", null, false, 17, AmbitoAplicacion.Empresa, true, "Vigencia variable (1, 3, 6 o 12 meses según lo que exija el cliente) — la fecha de vencimiento se introduce a mano al subir el documento."),
        (new Guid("20000000-0000-0000-0000-000000000003"), "ITA", 1, true, 18, AmbitoAplicacion.Empresa, true, "Mensual."),
        (new Guid("20000000-0000-0000-0000-000000000004"), "RLC/TC1", 3, true, 19, AmbitoAplicacion.Empresa, true, "Mensual — el documento de un periodo (p. ej. 01/05) vence 3 meses después (01/08), porque tarda en emitirse con la fecha del periodo ya pasada."),
        (new Guid("20000000-0000-0000-0000-000000000005"), "Recibo de pago RLC/TC1", 3, true, 20, AmbitoAplicacion.Empresa, true, "Mismo criterio de vigencia que el RLC/TC1."),
        (new Guid("20000000-0000-0000-0000-000000000006"), "RLC/TC1 + Recibo de pago", 3, true, 21, AmbitoAplicacion.Empresa, true, "Variante combinada — mismo criterio de vigencia que el RLC/TC1."),
        (new Guid("20000000-0000-0000-0000-000000000007"), "RNT/TC2", 3, true, 22, AmbitoAplicacion.Empresa, true, "Mismo criterio que el RLC/TC1."),
        (new Guid("20000000-0000-0000-0000-000000000008"), "Mutua", null, false, 23, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("20000000-0000-0000-0000-000000000009"), "Seguro de Responsabilidad Civil + recibo de pago", null, false, 24, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("2000000A-0000-0000-0000-00000000000A"), "SPA (Servicio de Prevención Ajeno)", null, false, 25, AmbitoAplicacion.Empresa, true, "Debe venir acompañado de un certificado de pago que indica la fecha fin de validez — se introduce esa fecha manualmente."),
        (new Guid("2000000B-0000-0000-0000-00000000000B"), "EVR (Evaluación de Riesgos Laborales)", null, false, 26, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("2000000C-0000-0000-0000-00000000000C"), "PAP (Planificación de la Actividad Preventiva)", null, false, 27, AmbitoAplicacion.Empresa, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("2000000D-0000-0000-0000-00000000000D"), "Tarjeta CIF", null, false, 28, AmbitoAplicacion.Empresa, false, "Opcional — no obligatorio para todos los clientes."),

        // --- Documentos de Vehículo (2026-07, indicados directamente por el usuario) ---
        (new Guid("30000000-0000-0000-0000-000000000001"), "ITC", null, false, 1, AmbitoAplicacion.Vehiculo, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("30000000-0000-0000-0000-000000000002"), "Ficha técnica", null, false, 2, AmbitoAplicacion.Vehiculo, true, "No caduca por sí sola, pero se pide como documento adjunto del vehículo."),
        (new Guid("30000000-0000-0000-0000-000000000003"), "Seguro", null, false, 3, AmbitoAplicacion.Vehiculo, true, "Vigencia sin especificar — fecha de vencimiento manual."),
        (new Guid("30000000-0000-0000-0000-000000000004"), "Autorización de circulación", null, false, 4, AmbitoAplicacion.Vehiculo, true, "Vigencia sin especificar — fecha de vencimiento manual."),
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
            Notas = d.Notas,
            AmbitoAplicacion = d.Ambito,
            EsObligatorio = d.EsObligatorio,
            LecturaIaActiva = true
        });
}
