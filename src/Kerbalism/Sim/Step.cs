﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KERBALISM
{
	public class Step
	{
		// step results
		public double bodiesCoreFlux;
		public StarFlux[] starFluxes = StarFlux.StarArrayFactory();

		// step parameters
		private SimBody[] bodies;
		private List<SimBody> occludingBodies = new List<SimBody>();
		private List<Vector3d> occludingBodiesDiff = new List<Vector3d>();
		private double ut;
		private bool landed;
		private Vector3d vesselPosition;
		private SimBody mainBody;
		private Vector3d mainBodyPosition;
		private Vector3d mainBodyDirection;
		private double altitude;
		private bool mainBodyIsVisible;
		private bool mainBodyIsMoon;
		private SimBody mainPlanet;
		private bool mainPlanetIsVisible;
		private Vector3d mainPlanetPosition;

		public double UT => ut;
		public double Altitude => altitude;

		public Step(SimVessel vessel, double ut = -1.0)
		{
			this.ut = ut;
			landed = vessel.landed;
			bodies = vessel.Bodies;
			vesselPosition = vessel.GetPosition(this);
			mainBody = vessel.mainBody;
			mainBodyPosition = mainBody.GetPosition(ut);
			mainBodyDirection = mainBodyPosition - vesselPosition;
			altitude = mainBodyDirection.magnitude;
			mainBodyDirection /= altitude;
			altitude -= mainBody.radius;

			occludingBodies.Clear();
			occludingBodiesDiff.Clear();

			foreach (SimBody occludingBody in bodies)
			{
				Vector3d bodyPosition = occludingBody.GetPosition(ut);

				// vector from ray origin to sphere center
				Vector3d difference = bodyPosition - vesselPosition;

				// if apparent diameter < ~10 arcmin (~0.003 radians), don't consider the body for occlusion checks
				// real apparent diameters at earth : sun/moon ~ 30 arcmin, Venus ~ 1 arcmin max
				if ((occludingBody.radius * 2.0) / difference.magnitude < 0.003)
					continue;

				occludingBodies.Add(occludingBody);
				occludingBodiesDiff.Add(difference);
			}

			mainBodyIsVisible = IsMainBodyVisible();
			mainBodyIsMoon = !mainBody.ReferenceBody.isSun;
			if (mainBodyIsMoon)
			{
				mainPlanet = mainBody.ReferenceBody;
				mainPlanetIsVisible = IsMainPlanetVisible();
				mainPlanetPosition = mainPlanet.GetPosition(ut);
			}
		}

		public void Evaluate()
		{
			AnalyzeSunFluxes();
			AnalyzeBodiesCoreFluxes();
		}

		public CelestialBody[] GetOccludingBodies()
		{
			CelestialBody[] bodies = new CelestialBody[occludingBodies.Count];

			for (int i = 0; i < occludingBodies.Count; i++)
				bodies[i] = occludingBodies[i].stockBody;

			return bodies;
		}

		private bool IsMainBodyVisible()
		{
			if (landed || (mainBody.hasAtmosphere && altitude < mainBody.atmosphereDepth))
				return true;

			for (int i = 0; i < occludingBodies.Count; i++)
			{
				if (occludingBodies[i] == mainBody)
					continue;

				if (Sim.RayHitSphere(occludingBodiesDiff[i], mainBodyDirection, occludingBodies[i].radius, altitude))
					return false;
			}

			return true;
		}

		private bool IsMainPlanetVisible()
		{
			Vector3d vesselToPlanet = mainPlanetPosition - vesselPosition;
			double distance = vesselToPlanet.magnitude;
			vesselToPlanet /= distance;

			for (int i = 0; i < occludingBodies.Count; i++)
			{
				if (occludingBodies[i] == mainPlanet)
					continue;

				if (Sim.RayHitSphere(occludingBodiesDiff[i], vesselToPlanet, occludingBodies[i].radius, distance))
					return false;
			}

			return true;
		}

		private void AnalyzeSunFluxes()
		{
			foreach (StarFlux starFlux in starFluxes)
			{
				SimBody sun = bodies[starFlux.Star.body.flightGlobalsIndex];

				Vector3d sunPosition = sun.GetPosition(ut);

				// generate ray parameters
				starFlux.direction = sunPosition - vesselPosition;
				starFlux.distance = starFlux.direction.magnitude;
				starFlux.direction /= starFlux.distance;

				bool isOccluded = false;
				for (int i = 0; i < occludingBodies.Count; i++)
				{
					SimBody occludingBody = occludingBodies[i];

					if (occludingBody == sun)
						continue;

					if (Sim.RayHitSphere(occludingBodiesDiff[i], starFlux.direction, occludingBody.radius, starFlux.distance))
					{
						isOccluded = true;
						break;
					}
				}

				// direct flux from this sun
				starFlux.directRawFlux = starFlux.Star.SolarFlux(starFlux.distance);

				if (isOccluded)
				{
					starFlux.directFlux = 0.0;
				}
				else
				{
					starFlux.directFlux = starFlux.directRawFlux;

					if (mainBody.hasAtmosphere && altitude < mainBody.atmosphereDepth)
						starFlux.directFlux *= Sim.AtmosphereFactor(mainBody, mainBodyPosition, starFlux.direction, vesselPosition, altitude);
				}

				// get indirect fluxes from bodies
				if (!mainBody.isSun)
				{
					if (mainBodyIsVisible)
					{
						Vector3d mainBodyToSun = sunPosition - mainBodyPosition;
						double mainBodyToSunDist = mainBodyToSun.magnitude;
						mainBodyToSun /= mainBodyToSunDist;

						bool mainBodyHasSunLoS = true;
						if (mainBodyIsMoon)
						{
							Vector3d moonToPlanet = mainPlanetPosition - mainBodyPosition;
							mainBodyHasSunLoS = !Sim.RayHitSphere(moonToPlanet, mainBodyToSun, mainPlanet.radius, mainBodyToSunDist);
						}

						GetBodyIndirectSunFluxes(starFlux, mainBody, mainBodyPosition, sunPosition, mainBodyToSunDist, mainBodyHasSunLoS);
					}

					// if main body is a moon, also consider fluxes from the planet
					if (mainBodyIsMoon && mainPlanetIsVisible)
					{
						double mainPlanetToSunDist = (sunPosition - mainPlanetPosition).magnitude;
						GetBodyIndirectSunFluxes(starFlux, mainPlanet, mainPlanetPosition, sunPosition, mainPlanetToSunDist, true);
					}
				}
			}
		}

		/// <summary>
		/// Get solar flux re-emitted by the body at the vessel position
		/// We work on the assumption that all solar flux blocked by the body disc
		/// is reflected back to space, either directly (albedo) or trough thermal re-emission.
		/// </summary>
		/// <param name="starFlux">Sun fluxes data to update</param>
		/// <param name="sunFluxAtBody">flux in W/m² received by this body from the considered sun</param>
		/// <param name="bodyIsVisibleFromSun">false if the sun LOS for a moon is blocked by it's parent planet</param>
		private void GetBodyIndirectSunFluxes(StarFlux starFlux, SimBody body, Vector3d bodyPosition, Vector3d sunPosition, double bodyToSunDist, bool bodyIsVisibleFromSun)
		{
			// Get solar flux re-emitted by the body at the vessel position
			// We work on the assumption that all solar flux blocked by the body disc
			// is reflected back to space, either directly (albedo) or trough thermal re-emission.
			double sunFluxAtBody = starFlux.Star.SolarFlux(bodyToSunDist);

			// ALBEDO
			double albedoFlux = 0.0;
			if (bodyIsVisibleFromSun)
			{
				// with r = body radius,
				// with a = altitude,
				// - The total energy received by the exposed surface area (disc) of the body is :
				// sunFluxAtBody * π * r²
				// - Assuming re-emitted power is spread over **one hemisphere**, that is a solid angle of :
				// 2 * π steradians
				// - So the energy emitted in watts per steradian can be expressed as :
				// sunFluxAtBody * π * r² / (2 * π * steradian)
				// - The sphere enclosing the body at the given altitude has a surface area of
				// 4 * π * (r + a)² 
				// - This translate in a surface area / steradian of
				// 4 * π * (r + a)² / (2 * π steradian) = (r + a)² / steradian
				// - So the flux received at the current altitude is :
				// sunFluxAtBody * π * r² / (2 * π * steradian) / ((r + a)² / steradian)
				// - Which can be simplified to :
				// (sunFluxAtBody * r²) / (2 * (r + a)²))
				double hemisphericFluxAtAltitude = (sunFluxAtBody * body.radius * body.radius) / (2.0 * Math.Pow(body.radius + altitude, 2.0));
				albedoFlux = hemisphericFluxAtAltitude * body.albedo;

				// ALDEBO COSINE FACTOR
				// the full albedo flux is received only when the vessel is positioned along the sun-body axis, and goes
				// down to zero on the night side.
				Vector3d bodyToSun = (sunPosition - bodyPosition).normalized;
				Vector3d bodyToVessel = (vesselPosition - bodyPosition).normalized;
				double anglefactor = (Vector3d.Dot(bodyToSun, bodyToVessel) + 1.0) / 2.0;
				// In addition of the crescent-shaped illuminated portion making the angle/flux relation non-linear,
				// the flux isn't scattered uniformely but tend to be reflected back in the sun direction,
				// especially on non atmospheric bodies, see https://en.wikipedia.org/wiki/Opposition_surge
				// We do some square scaling to approximate those effects, the choosen exponents seems to give results that 
				// aren't too far from RL data given by the JPL-HORIZONS data set : https://ssd.jpl.nasa.gov/horizons.cgi
				if (body.hasAtmosphere)
					albedoFlux *= Math.Pow(anglefactor, 2.0);
				else
					albedoFlux *= Math.Pow(anglefactor, 3.0);
			}

			// THERMAL RE-EMISSION
			// We account for this even if the body is currently occluded from the sun
			// We use the same formula, excepted re-emitted power is spread over the full
			// body sphere, that is a solid angle of 4 * π steradians
			// The end formula becomes :
			// (sunFluxAtBody * r²) / (4 * (r + a)²)
			double sphericFluxAtAltitude = (sunFluxAtBody * body.radius * body.radius) / (4.0 * Math.Pow(body.radius + altitude, 2.0));
			double emissiveFlux = sphericFluxAtAltitude * (1.0 - body.albedo);

			// if we are inside the atmosphere, scale down both fluxes by the atmosphere absorbtion at the current altitude
			// rationale : the atmosphere itself play a role in refracting the solar flux toward space, and the proportion of
			// the emissive flux released by the atmosphere itself is really only valid when you're in space. The real effects
			// are quite complex, this is a first approximation.
			if (body.hasAtmosphere && altitude < body.atmosphereDepth)
			{
				double atmoFactor = Sim.AtmosphereFactor(body, altitude);
				albedoFlux *= atmoFactor;
				emissiveFlux *= atmoFactor;
			}

			starFlux.bodiesAlbedoFlux += albedoFlux;
			starFlux.bodiesEmissiveFlux += emissiveFlux;
		}


		private void AnalyzeBodiesCoreFluxes()
		{
			if (mainBody.isSun)
				return;

			if (mainBodyIsVisible)
				bodiesCoreFlux += BodyCoreFlux(mainBody);

			// if main body is a moon, also consider core flux from the planet
			if (mainBodyIsMoon && mainPlanetIsVisible)
				bodiesCoreFlux += BodyCoreFlux(mainPlanet);
		}

		/// <summary>
		/// Some bodies emit an internal thermal flux due to various tidal, geothermal or accretional phenomenons
		/// This is given by CelestialBody.coreTemperatureOffset
		/// From that value we derive a sea level thermal flux in W/m² using the blackbody equation
		/// We assume that the atmosphere has no effect on that value.
		/// </summary>
		/// <returns>Flux in W/m2 at vessel altitude</returns>
		private double BodyCoreFlux(SimBody body)
		{
			if (body.coreThermalFlux == 0.0)
				return 0.0;

			// We use the same formula as GetBodiesIndirectSunFluxes for altitude scaling, but this time the 
			// coreThermalFlux is scaled with the body surface area : coreFluxAtSurface * 4 * π * r²
			// Resulting in the simplified formula :
			// (coreFluxAtSurface * r²) / (r + a)²
			return (body.coreThermalFlux * Math.Pow(body.radius, 2.0)) / Math.Pow(body.radius + altitude, 2.0);
		}
	}
}