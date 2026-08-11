# Circular informativa sobre medición de tiempos de gestión (art. 87 LOPDGDD)

**Tipo**: Plantilla para la organización cliente
**Estado**: Draft — **no ha pasado revisión legal**. Como el resto de `docs/business/legal/`, no es un documento utilizable frente a la plantilla ni ante una inspección hasta que un especialista lo revise.
**Destinatario**: la consultora o empresa que usa Hydra, no sus clientes finales.

## Por qué existe

El módulo de medición de tiempo de enfoque (`ParametroSistema.MedicionTiempoActiva`, apagado de fábrica) trata datos de rendimiento de personas trabajadoras. El art. 87.1 LOPDGDD y el art. 20.3 del Estatuto de los Trabajadores exigen **información previa** a la plantilla antes de activarlo, y el art. 64.5 ET exige además **consulta previa a la representación legal** de las personas trabajadoras.

Hydra no puede cumplir ninguna de esas dos obligaciones por su cliente: son actos de la organización empleadora. Lo que sí puede hacer, y hace aquí, es entregar el texto ya redactado para que activar el interruptor no dependa de que alguien improvise una circular.

Las garantías técnicas que el texto afirma (no hay keylogger, hay contador visible, hay pausa manual) son verificables en el código y están documentadas en `RGPD-TRATAMIENTO-DATOS.md` § 8. **Si alguna de ellas deja de ser cierta, esta circular deja de ser válida** y hay que revisarla antes de seguir usándola.

## Cómo usarla

Sustituir `[Nombre de la organización]` y la fecha. Entregarla por un medio que deje constancia de la recepción. Guardar esa constancia: es la prueba de haber informado.

---

## CIRCULAR INFORMATIVA SOBRE EL USO DE HERRAMIENTAS DIGITALES Y MEDICIÓN DE TIEMPOS DE GESTIÓN

**A la atención de:** la plantilla de personas trabajadoras de [Nombre de la organización]

**Fecha:** [fecha]

**Asunto:** deber de información sobre la implantación del módulo de gestión operativa y tiempos de enfoque en la plataforma Hydra (art. 87 LOPDGDD y art. 20.3 del Estatuto de los Trabajadores).

### 1. Objeto y finalidad

En cumplimiento de lo dispuesto en los artículos 87 y 90 de la Ley Orgánica 3/2018, de 5 de diciembre, de Protección de Datos Personales y garantía de los derechos digitales (LOPDGDD), y en el artículo 20.3 del Estatuto de los Trabajadores, la dirección informa de la implantación del módulo de **gestión de flujos y tiempos de enfoque de expedientes** dentro de la plataforma corporativa Hydra.

Las finalidades exclusivas de este tratamiento son:

1. **Optimización de la carga de trabajo:** equilibrar la asignación de clientes y expedientes entre los miembros del equipo para evitar situaciones de saturación o sobrecarga.
2. **Cálculo de reconocimientos:** servir de base, junto con las valoraciones cualitativas de los clientes, al programa de reconocimiento por calidad en la gestión.
3. **Acreditación ante el cliente:** justificar la dedicación efectiva y los recargos por solicitudes recibidas fuera de plazo.

### 2. Alcance y garantías de privacidad

Se informa expresamente de que **Hydra no utiliza software de vigilancia ni monitorización de periféricos**:

- **No se recopila** información del teclado, coordenadas ni movimientos del ratón, patrones de desplazamiento, ni capturas de pantalla.
- **No se realiza** ningún seguimiento fuera de la aplicación web corporativa.
- **Lo único que se registra** es el intervalo de tiempo durante el cual un expediente concreto permanece abierto y activo en pantalla mientras la ventana de trabajo mantiene el foco, y la suma de segundos de ese intervalo.

### 3. Control y transparencia para la persona trabajadora

- **Visibilidad en tiempo real:** el tiempo contabilizado para cada tarea es visible en todo momento en la propia interfaz. No hay medición oculta.
- **Botón de pausa:** puede pausarse manualmente el registro en cualquier momento —para llamadas, descansos, reuniones o gestiones fuera del sistema— sin necesidad de justificación previa.
- **Pausa automática:** si la ventana pierde el foco o transcurre el umbral de inactividad configurado, el contador se detiene solo. Ningún tramo acumula más de 30 minutos continuos.

### 4. Derechos

Puede ejercerse el derecho de acceso, rectificación, supresión, limitación y oposición sobre los datos de rendimiento registrados, así como solicitar aclaraciones sobre la asignación de cargas de trabajo, dirigiéndose a [contacto designado por la organización].

---

## Cuestiones abiertas para la revisión legal

1. **Suficiencia del interés legítimo (art. 6.1.f RGPD) como base.** Se ha descartado el consentimiento por no considerarse libremente prestado en el marco de una relación laboral. Confirmar, y valorar si procede documentar una ponderación de intereses.
2. **Alcance de la consulta previa a la representación legal (art. 64.5 ET)** cuando la organización no tiene comité de empresa ni delegados de personal.
3. **Si procede una Evaluación de Impacto (EIPD).** El tratamiento no encaja de forma evidente en los supuestos del art. 35.3 RGPD, pero la lista de la AEPD sobre tratamientos que requieren EIPD incluye la evaluación sistemática de aspectos del rendimiento laboral. Decisión no tomada.
4. **Uso en decisiones sobre la persona.** El texto habla de "reconocimiento". Si estos datos llegaran a intervenir en decisiones disciplinarias, de promoción o de extinción, el régimen cambia y esta circular no sería suficiente.
