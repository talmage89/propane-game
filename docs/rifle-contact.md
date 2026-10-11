# Rifle contact

The rifle rig places a rigid rifle before solving the arms. Its wrist offsets and orientations are fitted to
the mannequin palms, the traced pistol grip and the vertical foregrip. A 25 mm reach margin leaves both elbows
bent: translation projects the two wrist targets into the intersection of the arms' reachable spheres. IK uses
the actual targets and reports unreachable targets or residuals above 3 mm instead of silently shortening the
reach.

The upper-body animation filter includes the three spine bones so run and sprint cannot swing the chest away
from the contacts. The rig distributes one world-space torso rotation across those bones, including camera
pitch. Aim, ready and sprint have a separate visual blend in the rig: logical aim can clear immediately on
release, jump, sprint or reload without snapping the rifle or shoulders. Lower-body gait selection, speed and clip timing are unchanged.

Carry and sprint keep a dipped, inboard muzzle; ready points from the hip. Aim seats the butt at the right
shoulder pocket, raises the clavicle and fits the neck/head to the authored `Socket_Eye`. The fit rotates
bones without stretching them. The get-up handoff keeps the firing wrist attached and projects the gradually
returning support wrist into reach after blending the rifle. The right-eye landmark is calibrated to the
mannequin head, rather than its origin. Recoil moves the rifle off the settled eye line briefly; the wrists
follow the moving rifle.

Each finger has three absolute curls relative to its authored rest frame. The firing index rests along the
receiver and curls toward the trigger during recoil. Reload uses separate magazine curls and follows the
magazine's orientation. The hand opens away from the magazine and receiver before moving back; its thumb stays
wrapped until clear, then closes around the foregrip. Empty reload also approaches the bolt catch from
outside. The source rifle mesh, sockets, clips and their event times are unchanged. The ragdoll hand follower
reads the same live firing-wrist calibration as the rig.

Placement, recoil, wrist calibration, eye landmark, finger curls and reload clearance are `Tuning` values in
**Rifle Contact**. These values join the existing shared tuning snapshot: the next release must be minor. No
messages changed, and `Protocol.Version` remains 2.
