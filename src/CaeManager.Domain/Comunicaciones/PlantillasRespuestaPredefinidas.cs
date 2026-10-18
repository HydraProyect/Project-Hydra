using System.Security.Cryptography;
using System.Text;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>Contenido inicial por Tenant propietario. Nunca incorpora datos de las conversaciones.</summary>
public static class PlantillasRespuestaPredefinidas
{
    public static IReadOnlyList<MacroRespuesta> Crear(Guid tenantPropietarioId)
    {
        if (tenantPropietarioId == Guid.Empty)
            throw new ArgumentException("La siembra requiere un Tenant propietario.", nameof(tenantPropietarioId));

        (string Codigo, string Titulo, string Cuerpo)[] definiciones =
        [
            ("primer-contacto", "Primer contacto y requisitos de acceso",
                "Buenos días. Contactamos en nombre de {{Empresa}} para la visita prevista el {{fecha de la visita}} al Centro {{Centro}} de {{Cliente empresarial}}. ¿Podéis confirmar los requisitos de acceso y si debemos enviar la documentación por correo o subirla a una plataforma? En ese caso, indicadnos la plataforma y cómo obtener acceso."),
            ("pendientes", "Solicitud de documentación pendiente",
                "Buenos días. Para completar la documentación de {{Empresa}} en {{Centro}} de {{Cliente empresarial}}, necesitamos: {{documentos pendientes}}. ¿Podéis enviarla o indicar cuándo estará disponible?"),
            ("mensual", "Solicitud de documentación mensual",
                "Buenos días. Solicitamos la documentación mensual de {{Empresa}} requerida por {{Cliente empresarial}} para {{Centro}}: {{documentos pendientes}}. Revisad que corresponda al periodo solicitado e indicadnos cuándo estará disponible."),
            ("extranjero", "Requisitos para Trabajador extranjero",
                "Buenos días. Para revisar el acceso de {{Trabajador}} de {{Empresa}} a {{Centro}}, necesitamos confirmar los requisitos aplicables al desplazamiento. Remitid el certificado A1 cuando corresponda y los demás certificados exigidos por {{Cliente empresarial}}: {{documentos pendientes}}. Si algún requisito no resulta aplicable, indicadlo para su revisión."),
            ("subida", "Documentación subida a la plataforma",
                "Buenos días. Hemos subido a la plataforma la documentación de {{Empresa}} para {{Centro}} de {{Cliente empresarial}}. ¿Podéis confirmar su recepción y revisión? Queda pendiente: {{documentos pendientes}}."),
            ("autorizado", "Acceso autorizado con credencial o QR",
                "Buenos días. Confirmamos el acceso autorizado de {{Trabajador}} de {{Empresa}} a {{Centro}} de {{Cliente empresarial}} para el {{fecha de la visita}}. Adjuntamos la credencial o el QR de acceso. Revisad el archivo adjunto y las instrucciones del Centro antes de la visita."),
            ("acceso-pendiente", "Acceso pendiente por formulario o documentación vencida",
                "Buenos días. El acceso de {{Trabajador}} de {{Empresa}} a {{Centro}} de {{Cliente empresarial}} sigue pendiente. Falta completar un formulario o renovar documentación vencida: {{documentos pendientes}}. Necesitamos recibirla y confirmar su validación antes de comunicar la autorización de acceso."),
            ("trabajadores", "Solicitud de documentación de Trabajadores a la Empresa",
                "Buenos días. Solicitamos a {{Empresa}} la documentación de {{Trabajador}} para {{Centro}} de {{Cliente empresarial}}: {{documentos pendientes}}. Enviad los documentos vigentes e identificad a qué Trabajador corresponde cada archivo."),
            ("general", "Solicitud general de documentación y datos",
                "Buenos días. Para continuar con la coordinación de {{Empresa}} en {{Centro}} de {{Cliente empresarial}}, necesitamos lo siguiente: {{documentos pendientes}}. Indicad cualquier aclaración necesaria y la previsión de entrega de cada elemento.")
        ];

        return definiciones.Select(d =>
        {
            // Identidad estable: reiniciar no repone una plantilla editada ni una eliminada.
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"TALVEG:plantilla-respuesta:v1:{tenantPropietarioId:D}:{d.Codigo}"));
            return MacroRespuesta.CrearPredefinida(new Guid(bytes.AsSpan(0, 16)), d.Titulo, d.Cuerpo);
        }).ToList();
    }
}
