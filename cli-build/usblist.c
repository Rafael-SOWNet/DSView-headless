#include <stdio.h>
#include <libusb-1.0/libusb.h>

int main(void) {
    libusb_context *ctx = NULL;
    if (libusb_init(&ctx) < 0) { printf("init failed\n"); return 1; }
    libusb_device **list;
    ssize_t n = libusb_get_device_list(ctx, &list);
    printf("total usb devices: %zd\n", n);
    for (ssize_t i = 0; i < n; i++) {
        struct libusb_device_descriptor d;
        if (libusb_get_device_descriptor(list[i], &d) != 0) continue;
        if (d.idVendor == 0x2A0E) {
            uint8_t bus = libusb_get_bus_number(list[i]);
            uint8_t addr = libusb_get_device_address(list[i]);
            int speed = libusb_get_device_speed(list[i]);
            printf("FOUND vid=%04x pid=%04x bus=%u addr=%u speed=%d\n",
                   d.idVendor, d.idProduct, bus, addr, speed);
            libusb_device_handle *h = NULL;
            int r = libusb_open(list[i], &h);
            printf("  libusb_open -> %d (%s)\n", r, libusb_error_name(r));
            if (h) libusb_close(h);
        }
    }
    libusb_free_device_list(list, 1);
    libusb_exit(ctx);
    return 0;
}
