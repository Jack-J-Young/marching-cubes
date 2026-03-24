#[compute]
#version 450

// Workgroup size
layout(local_size_x = 1, local_size_y = 1, local_size_z = 1) in;

// 3D input texture (density field)
layout(r8_snorm, set = 0, binding = 0) uniform image3D density_tex;
layout(std430, set = 0, binding = 1) buffer dig_buffer {
  vec3 pos;
  float radius;
}dig_data;

void main() {
  // ivec3 offset = ivec3(0, 0, 0);
  ivec3 uv = ivec3(gl_GlobalInvocationID.xyz);
  // imageSize(img);

  vec4 color = imageLoad(density_tex, uv);

  float dist = distance(dig_data.pos, uv)/dig_data.radius;
  color *= clamp(dist, 0, 1);

  imageStore(density_tex, uv, color);
}