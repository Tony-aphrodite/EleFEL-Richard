# Día 5 - Reporte de Avance EleFEL Connector

**Fecha:** [Fecha de inicio + 4]
**Fase:** Motor principal + Bandeja del sistema + Opción "Facturar Después"

---

## Trabajo completado hoy

### 1. Motor principal (EleFelEngine)
- Orquestador central que conecta todos los servicios del sistema
- Flujo completo automatizado:
  1. Inicia monitoreo de base de datos Eleventa
  2. Detecta nueva venta → muestra ventana de NIT
  3. Cajero elige una opción → sistema responde según la acción
  4. Si falla la conexión → agrega a cola → reintenta automáticamente
- Control de duplicados: verifica que cada venta se procese solo una vez
- Limpieza automática de ventas pendientes expiradas al iniciar el sistema

### 2. Aplicación en bandeja del sistema
- El conector corre en segundo plano sin interferir con Eleventa
- Icono en la bandeja del sistema de Windows con indicadores de estado:
  - **Verde** → Sistema activo, todas las facturas al día
  - **Amarillo** → Facturas pendientes de certificación
  - **Rojo** → Error en el sistema
- Menú contextual con opciones:
  - Estado actual del conector
  - **Ventas Pendientes de Facturar** (nueva opción)
  - Abrir carpeta de facturas (XML/PDF)
  - Abrir carpeta de logs
  - Salir de la aplicación
- Notificaciones tipo globo cuando hay facturas pendientes

### 3. Opción "Facturar Después" implementada
Según lo solicitado, la ventana de captura de NIT ahora ofrece **4 opciones** para el cajero:

| Acción | Tecla | Descripción |
|---|---|---|
| **Facturar** | Enter | Ingresa NIT y genera factura electrónica |
| **Consumidor Final** | F2 | Genera factura con NIT genérico "CF" |
| **Facturar Después** | F3 | Guarda la venta como pendiente para facturar en otro momento |
| **Cancelar** | Esc | Cierra la ventana (la venta se guarda como pendiente) |

Comportamientos adicionales implementados:
- **Si el cajero cierra la ventana con la X** → la venta se guarda automáticamente como pendiente (no se pierde ninguna venta)
- **Si presiona Cancelar (Esc)** → la venta también se guarda como pendiente
- La facturación es **completamente opcional**: el cajero puede elegir no facturar sin afectar el funcionamiento de Eleventa

### 4. Expiración automática de ventas pendientes
- Las ventas guardadas como "Facturar Después" se eliminan automáticamente después de **3 días** (configurable en config.json)
- La limpieza se ejecuta automáticamente cada vez que el sistema se inicia
- Esto evita que la lista de pendientes crezca indefinidamente

### 5. Nuevo estado "Postponed" en el sistema
- Se agregó el estado `Postponed` (5) al modelo de facturas
- Las ventas pendientes se almacenan en la base de datos SQLite local con este estado
- Se distinguen claramente de las facturas con error de certificación (que usan la cola de reintentos)

---

## Estado actual

| Componente | Estado |
|---|---|
| Estructura + Modelos + Config | ✅ Completado (Día 1) |
| Conexión Firebird + SQLite | ✅ Completado (Día 1) |
| Ventana NIT + Autocompletado + Logs | ✅ Completado (Día 2) |
| XML DTE + API Infile | ✅ Completado (Día 3) |
| PDF + Impresión + Cola offline | ✅ Completado (Día 4) |
| Motor principal (EleFelEngine) | ✅ Completado |
| Bandeja del sistema + indicadores | ✅ Completado |
| Opción "Facturar Después" (F3) | ✅ Completado |
| Auto-guardar al cerrar ventana | ✅ Completado |
| Expiración automática (3 días) | ✅ Completado |

## Próximo paso (Día 6)
- Pantalla de gestión de ventas pendientes (facturar / eliminar)
- Pruebas integrales del flujo completo
- Preparación del paquete de entrega final

---

*EleFEL Connector - Reporte diario de desarrollo*
